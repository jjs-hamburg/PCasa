using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PCasa.App.Services;

/// <summary>
/// One in-memory edit transaction per JPEG. Crop and rotation share a single
/// cursor; no original is modified until the user's explicit Save command.
/// The same operations are replayed at full resolution by the P0 writer.
/// </summary>
public sealed record PhotoEditStep(
    string Kind, double X = 0, double Y = 0,
    double Width = 1, double Height = 1, double Degrees = 0);

public sealed class PhotoEditSession
{
    private readonly BitmapSource _original;
    private readonly List<PhotoEditStep> _steps = [];
    private int _cursor;
    public long ImageId { get; }

    public PhotoEditSession(long imageId, BitmapSource original)
    {
        ImageId = imageId;
        _original = original ?? throw new ArgumentNullException(nameof(original));
    }
    public bool HasChanges => _cursor > 0;
    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _steps.Count;
    public IReadOnlyList<PhotoEditStep> AppliedSteps =>
        _steps.Take(_cursor).ToArray();

    public void Apply(PhotoEditStep step)
    {
        if (_cursor < _steps.Count)
            _steps.RemoveRange(_cursor, _steps.Count - _cursor);
        _steps.Add(step);
        _cursor++;
    }
    public void Undo() { if (CanUndo) _cursor--; }
    public void Redo() { if (CanRedo) _cursor++; }
    public BitmapSource Render() => Render(_original, AppliedSteps);

    public static BitmapSource Render(BitmapSource original,
        IEnumerable<PhotoEditStep> steps)
    {
        BitmapSource bitmap = original;
        foreach (var step in steps)
        {
            if (step.Kind == "crop")
            {
                if (!double.IsFinite(step.X) || !double.IsFinite(step.Y) ||
                    !double.IsFinite(step.Width) || !double.IsFinite(step.Height) ||
                    step.Width < .01 || step.Height < .01 ||
                    step.X < 0 || step.Y < 0 ||
                    step.X + step.Width > 1.000001 ||
                    step.Y + step.Height > 1.000001)
                    throw new InvalidDataException("Ungültiger Crop-Bereich.");
                var x = Math.Clamp((int)Math.Floor(step.X * bitmap.PixelWidth),
                    0, bitmap.PixelWidth - 1);
                var y = Math.Clamp((int)Math.Floor(step.Y * bitmap.PixelHeight),
                    0, bitmap.PixelHeight - 1);
                var width = Math.Clamp((int)Math.Round(step.Width * bitmap.PixelWidth),
                    1, bitmap.PixelWidth - x);
                var height = Math.Clamp((int)Math.Round(step.Height * bitmap.PixelHeight),
                    1, bitmap.PixelHeight - y);
                bitmap = new CroppedBitmap(bitmap, new Int32Rect(x, y, width, height));
            }
            else if (step.Kind == "rotate")
            {
                if (!double.IsFinite(step.Degrees) || Math.Abs(step.Degrees) > 360)
                    throw new InvalidDataException("Ungültiger Rotationswinkel.");
                if (Math.Abs(step.Degrees) < .00001) continue;
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var rotated = new TransformedBitmap(bitmap,
                    new RotateTransform(step.Degrees));
                rotated.Freeze();
                bitmap = rotated;
                if (Math.Abs(step.Degrees % 90) > .00001)
                {
                    // Largest safe rectangle with the original aspect ratio.
                    // Every corner remains inside the rotated source: no
                    // black triangles at any intermediate angle.
                    var rad = Math.Abs(step.Degrees * Math.PI / 180);
                    var sin = Math.Abs(Math.Sin(rad));
                    var cos = Math.Abs(Math.Cos(rad));
                    var ratio = width / (double)height;
                    var safeHeight = Math.Min(width / (ratio * cos + sin),
                                               height / (ratio * sin + cos));
                    var safeWidth = ratio * safeHeight;
                    var w = Math.Clamp((int)Math.Floor(safeWidth) - 2,
                        1, bitmap.PixelWidth);
                    var h = Math.Clamp((int)Math.Floor(safeHeight) - 2,
                        1, bitmap.PixelHeight);
                    bitmap = new CroppedBitmap(bitmap, new Int32Rect(
                        (bitmap.PixelWidth - w) / 2,
                        (bitmap.PixelHeight - h) / 2, w, h));
                }
            }
            else throw new InvalidDataException(
                "Unbekannte Bildbearbeitung: " + step.Kind);
            if (bitmap.PixelWidth < 16 || bitmap.PixelHeight < 16)
                throw new InvalidDataException("Das bearbeitete Bild ist zu klein.");
            bitmap.Freeze();
        }
        return bitmap;
    }
}
