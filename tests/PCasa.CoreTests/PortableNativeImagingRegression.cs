using NetVips;
using PCasa.App.Data;
using PCasa.App.Models;
using PCasa.App.Services;

internal static class PortableNativeImagingRegression
{
    public static void Run(string directory)
    {
        var sandbox = Path.Combine(directory, "native-imaging");
        var originals = Path.Combine(sandbox, "originals");
        var thumbs = Path.Combine(sandbox, "thumbs");
        var packs = Path.Combine(sandbox, "packs");
        Directory.CreateDirectory(originals);
        NativeImageCachePolicy.EnsureConfigured();

        // Real portrait source large enough to exercise every size tier.
        var jpeg = Path.Combine(originals, "portrait.jpg");
        var png = Path.Combine(originals, "transparent.png");
        var psd = Path.Combine(originals, "composite.psd");
        const int width = 200, height = 400;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            pixels[i] = 180;
            pixels[i + 1] = (byte)(x % 255);
            pixels[i + 2] = 30;
            pixels[i + 3] = 192;
        }
        using (var memory = Image.NewFromMemory(pixels, width, height, 4, Enums.BandFormat.Uchar))
        using (var color = memory.Copy(interpretation: Enums.Interpretation.Srgb))
        {
            color.Pngsave(png);
            using var flat = color.Flatten(background: new[] { 255.0, 255.0, 255.0 });
            flat.Jpegsave(jpeg, q: 90);
        }
        File.Copy(Path.Combine(directory, "fixture-0.psd"), psd);

        // Native ExifTool write/read, followed by the real portable scanner.
        using (var session = new ExifToolKeywordWriter.Session())
        {
            session.Write(jpeg, new[] { "portable native test" });
            if (!session.Read(jpeg).Contains("portable native test"))
                throw new Exception("Native keyword round-trip failed.");
        }
        var before = Directory.EnumerateFiles(originals)
            .ToDictionary(path => path, File.ReadAllBytes);
        var database = new LocalDatabase(Path.Combine(sandbox, "native.db"));
        database.Initialize();
        var roots = new FolderRepository(database);
        roots.Add(originals);
        var root = roots.GetAll().Single();
        var catalog = new ImageCatalogRepository(database);
        var profiles = new BrowseThumbnailProfileRepository(database);
        var media = new PortableImportMedia(thumbs, packs, Path.Combine(sandbox, "psd"));
        var scanner = new FolderScanner(catalog, media, roots, profiles);
        var imported = scanner.Scan(root);
        if (imported.Updated != 3 || imported.Failed != 0)
            throw new Exception($"Native import failed: {imported}");
        var jpegId = catalog.GetImageId(jpeg) ?? throw new Exception("Native JPEG not indexed.");
        var tags = new TagCatalogOperations(database);
        if (tags.GetKeywords(jpegId)?.Contains("portable native test") != true)
            throw new Exception("Native keywords were not imported.");

        var index = new BrowseCacheRepository(database);
        using (var packed = new PackedThumbnailBytes(packs))
        foreach (var tier in profiles.Get().Tiers)
        {
            var entries = index.GetIndex(tier);
            if (entries.Count != 3)
                throw new Exception($"Expected three real thumbnails in tier {tier}.");
            foreach (var entry in entries)
            {
                var bytes = packed.Load(entry) ?? throw new Exception("Missing packed bytes.");
                using var decoded = Image.NewFromBuffer(bytes);
                if (decoded.Width != entry.PixelWidth || decoded.Height != entry.PixelHeight ||
                    decoded.Width >= decoded.Height || decoded.HasAlpha())
                    throw new Exception("Packed thumbnail dimensions, orientation or alpha are wrong.");
            }
        }

        var unchanged = scanner.Scan(root);
        if (unchanged.Updated != 0 || unchanged.Unchanged != 3 || unchanged.Failed != 0)
            throw new Exception("Native import rebuilt unchanged originals.");

        var placeholder = media.CreateImportErrorPlaceholder(
            Path.Combine(originals, "invalid.jpg"), 1, 1, CancellationToken.None);
        using (var decoded = Image.NewFromFile(placeholder.Path))
            if (decoded.Width != 256 || decoded.Height != 256)
                throw new Exception("Portable error preview failed to encode.");

        foreach (var (path, bytes) in before)
            if (!File.ReadAllBytes(path).SequenceEqual(bytes))
                throw new Exception("Read-only import modified an original.");
        if (Directory.EnumerateFiles(originals).Count() != before.Count)
            throw new Exception("Import created a file alongside an original.");

        Console.WriteLine("PASS: native libvips JPEG, PNG, PSD, packed tiers, error preview and ExifTool metadata.");
    }
}
