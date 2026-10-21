using System.IO;
using NetVips;
using PCasa.App.Models;

namespace PCasa.App.Services;

public sealed class PackedThumbnailStore : IDisposable
{
    private const long MaxPackBytes = 256L * 1024 * 1024;

    private readonly string _cacheRoot;
    private readonly Dictionary<int, PackWriter> _writers = new();
    private bool _disposed;

    public PackedThumbnailStore()
    {
        _cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa",
            "browse-cache");

        Directory.CreateDirectory(_cacheRoot);
    }

    public string CacheRoot => _cacheRoot;

    public static void ResetPackFiles()
    {
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCasa",
            "browse-cache");

        if (!Directory.Exists(cacheRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(
                     cacheRoot,
                     "*.pack",
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<PackedThumbnailLocation> CreateTiers(
        string sourcePath,
        BrowseThumbnailProfile profile,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var tiers = profile.Tiers
            .Distinct()
            .OrderBy(tier => tier)
            .ToArray();
        if (tiers.Length == 0)
        {
            return Array.Empty<PackedThumbnailLocation>();
        }

        foreach (var tier in tiers)
        {
            if (tier < 48 || tier > 2048)
            {
                throw new ArgumentOutOfRangeException(nameof(profile));
            }
        }

        // Do not reuse a sequential libvips file source for several independently
        // evaluated resize pipelines. The first evaluation consumes the sequential
        // source and subsequent tiers can then fail. Instead, decode the source once
        // to the largest required browse size and materialize that small image in RAM.
        var largestTier = tiers[^1];
        using var largestPipeline = Image.Thumbnail(
            sourcePath,
            largestTier,
            height: largestTier,
            size: Enums.Size.Down);

        cancellationToken.ThrowIfCancellationRequested();

        using var preparedPipeline = largestPipeline.HasAlpha()
            ? largestPipeline.Flatten(background: new[] { 255.0, 255.0, 255.0 })
            : largestPipeline.Copy();
        using var residentSource = preparedPipeline.CopyMemory();

        // Encode every requested tier first. Only after all tiers succeeded are bytes
        // appended to pack files. A conversion failure can therefore no longer leave
        // a half-created image entry in the pack store.
        var prepared = new List<PreparedTier>(tiers.Length);
        foreach (var tier in tiers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var tierImage = CreateTierImage(residentSource, tier);
            var bytes = tierImage.JpegsaveBuffer(
                q: TierQuality(tier),
                optimizeCoding: true,
                keep: Enums.ForeignKeep.None);

            prepared.Add(new PreparedTier(
                tier,
                bytes,
                tierImage.Width,
                tierImage.Height));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<PackedThumbnailLocation>(prepared.Count);
        foreach (var tier in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(Append(
                tier.Tier,
                tier.Bytes,
                tier.Width,
                tier.Height));
        }

        return result;
    }

    public PackedThumbnailLocation CreateTier(
        string sourcePath,
        int tier,
        CancellationToken cancellationToken = default)
    {
        var result = CreateTiers(
            sourcePath,
            new BrowseThumbnailProfile(new[] { tier }),
            cancellationToken);
        return result[0];
    }

    private static Image CreateTierImage(Image source, int tier)
    {
        var longestSide = Math.Max(source.Width, source.Height);
        if (longestSide <= tier)
        {
            return source.Copy();
        }

        var scale = (double)tier / longestSide;
        return source.Resize(scale, kernel: Enums.Kernel.Lanczos3);
    }

    private PackedThumbnailLocation Append(
        int tier,
        byte[] bytes,
        int width,
        int height)
    {
        var writer = GetWriter(tier, bytes.Length);
        var offset = writer.Stream.Position;
        writer.Stream.Write(bytes, 0, bytes.Length);
        writer.Stream.Flush(flushToDisk: false);

        return new PackedThumbnailLocation(
            tier,
            Path.GetFileName(writer.Path),
            offset,
            bytes.Length,
            width,
            height);
    }

    private PackWriter GetWriter(int tier, int incomingBytes)
    {
        if (_writers.TryGetValue(tier, out var existing) &&
            existing.Stream.Position + incomingBytes <= MaxPackBytes)
        {
            return existing;
        }

        existing?.Dispose();

        var nextPath = ResolveCurrentOrNextPackPath(tier, incomingBytes);
        var stream = new FileStream(
            nextPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 256 * 1024,
            options: FileOptions.SequentialScan);
        stream.Position = stream.Length;

        var writer = new PackWriter(nextPath, stream);
        _writers[tier] = writer;
        return writer;
    }

    private string ResolveCurrentOrNextPackPath(int tier, int incomingBytes)
    {
        var prefix = $"thumbs-{tier}-";
        var existing = Directory
            .EnumerateFiles(_cacheRoot, $"{prefix}*.pack", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing.Count > 0)
        {
            var last = existing[^1];
            var length = new FileInfo(last).Length;
            if (length + incomingBytes <= MaxPackBytes)
            {
                return last;
            }
        }

        var nextNumber = existing.Count == 0
            ? 1
            : ParsePackNumber(existing[^1], prefix) + 1;

        return Path.Combine(_cacheRoot, $"{prefix}{nextNumber:000000}.pack");
    }

    private static int ParsePackNumber(string path, string prefix)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var numberText = fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? fileName[prefix.Length..]
            : string.Empty;

        return int.TryParse(numberText, out var number) ? number : 0;
    }

    private static int TierQuality(int tier)
        => tier switch
        {
            <= 128 => 72,
            <= 256 => 76,
            <= 512 => 80,
            <= 1024 => 84,
            _ => 86
        };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }

        _writers.Clear();
    }

    private sealed record PreparedTier(
        int Tier,
        byte[] Bytes,
        int Width,
        int Height);

    private sealed class PackWriter : IDisposable
    {
        public PackWriter(string path, FileStream stream)
        {
            Path = path;
            Stream = stream;
        }

        public string Path { get; }
        public FileStream Stream { get; }

        public void Dispose()
            => Stream.Dispose();
    }
}
