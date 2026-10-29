using System.Security.Cryptography;
using System.Text;

namespace PCasa.App.Services;

/// <summary>
/// User-defined map endpoints are data, not executable browser scripts.
/// Secrets use Windows DPAPI, never plaintext in appearance.json.
/// Only registered MapLibre styles and raster XYZ sources are supported.
/// </summary>
public sealed class GpsMapProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Format { get; set; } = "style";
    public string Url { get; set; } = "";
    public string Attribution { get; set; } = "";
    public string ProtectedKey { get; set; } = "";

    public static bool IsValid(GpsMapProvider provider)
        => !string.IsNullOrWhiteSpace(provider.Name) &&
           provider.Name.Length <= 80 &&
           provider.Format is "style" or "xyz" &&
           Uri.TryCreate(provider.Url.Replace("{key}", "key")
                .Replace("{z}", "1").Replace("{x}", "1").Replace("{y}", "1"),
                UriKind.Absolute, out var uri) &&
           uri.Scheme == Uri.UriSchemeHttps &&
           !uri.IsLoopback &&
           (provider.Format != "xyz" ||
                (provider.Url.Contains("{z}", StringComparison.Ordinal) &&
                 provider.Url.Contains("{x}", StringComparison.Ordinal) &&
                 provider.Url.Contains("{y}", StringComparison.Ordinal)));

    public static string EncryptKey(string key)
        => string.IsNullOrEmpty(key) ? "" : Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null,
                DataProtectionScope.CurrentUser));

    public string ResolveUrl()
    {
        if (!IsValid(this)) throw new InvalidOperationException("Ungültige Kartenquelle.");
        if (!Url.Contains("{key}", StringComparison.Ordinal)) return Url;
        if (string.IsNullOrEmpty(ProtectedKey))
            throw new InvalidOperationException("Der Kartenanbieter benötigt einen API-Schlüssel.");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedKey),
            null, DataProtectionScope.CurrentUser);
        return Url.Replace("{key}", Uri.EscapeDataString(Encoding.UTF8.GetString(bytes)),
            StringComparison.Ordinal);
    }
}
