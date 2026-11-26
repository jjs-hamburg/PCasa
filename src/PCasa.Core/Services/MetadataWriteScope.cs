namespace PCasa.App.Services;

internal static class MetadataWriteScope
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal static IDisposable Begin()
    {
        var scope = new Scope(Current.Value);
        Current.Value = scope;
        return scope;
    }

    private sealed class Scope(Scope? previous) : IDisposable
    {
        internal ExifToolKeywordWriter.Session? Writer;
        internal ImageExifReader? Reader;
        public void Dispose()
        {
            Current.Value = previous;
            try { Writer?.Dispose(); }
            finally { Reader?.Dispose(); }
        }
    }

    internal sealed class Lease<T>(T value, bool owns) : IDisposable where T : IDisposable
    {
        internal T Value { get; } = value;
        public void Dispose() { if (owns) Value.Dispose(); }
    }

    internal static Lease<ExifToolKeywordWriter.Session> RentWriter()
    {
        var scope = Current.Value;
        return scope is null ? new(new ExifToolKeywordWriter.Session(), true)
            : new(scope.Writer ??= new ExifToolKeywordWriter.Session(), false);
    }

    internal static Lease<ImageExifReader> RentReader()
    {
        var scope = Current.Value;
        return scope is null ? new(new ImageExifReader(), true)
            : new(scope.Reader ??= new ImageExifReader(), false);
    }
}
