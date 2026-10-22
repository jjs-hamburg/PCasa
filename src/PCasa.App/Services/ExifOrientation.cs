using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// The file remains untouched. WIC loads JPEG pixels in storage orientation;
/// libvips Thumbnail() already honors the EXIF flag. Apply the orientation
/// only to WIC's viewer bitmap, never to an already oriented cache preview.
/// </summary>
public static class ExifOrientation
{
    public static BitmapSource Apply(BitmapSource decoded, Stream originalStream)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(originalStream);
        var orientation = Read(originalStream);
        if (orientation is < 2 or > 8) return decoded;

        Transform transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => Compose(new RotateTransform(90), new ScaleTransform(-1, 1)),
            6 => new RotateTransform(90),
            7 => Compose(new RotateTransform(90), new ScaleTransform(1, -1)),
            8 => new RotateTransform(270),
            _ => Transform.Identity
        };
        var oriented = new TransformedBitmap(decoded, transform);
        oriented.Freeze();
        return oriented;
    }

    public static int Read(Stream originalStream)
    {
        if (!originalStream.CanSeek) return 1;
        var position = originalStream.Position;
        try
        {
            originalStream.Position = 0;
            var decoder = BitmapDecoder.Create(originalStream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var metadata = decoder.Frames[0].Metadata as BitmapMetadata;
            if (metadata is null) return 1;
            object? value = null;
            foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                if (!metadata.ContainsQuery(query)) continue;
                value = metadata.GetQuery(query);
                if (value is not null) break;
            }
            if (value is null) return 1;
            var orientation = Convert.ToInt32(value);
            return orientation is >= 1 and <= 8 ? orientation : 1;
        }
        catch (Exception exception) when (exception is NotSupportedException or
            FileFormatException or IOException or ArgumentException or
            System.Runtime.InteropServices.COMException)
        {
            // Metadata is optional: preserve the normal image even when an
            // unusual or corrupt metadata block cannot be decoded by WIC.
            return 1;
        }
        finally
        {
            originalStream.Position = position;
        }
    }

    private static Transform Compose(Transform first, Transform second)
    {
        var group = new TransformGroup();
        group.Children.Add(first);
        group.Children.Add(second);
        group.Freeze();
        return group;
    }
}
