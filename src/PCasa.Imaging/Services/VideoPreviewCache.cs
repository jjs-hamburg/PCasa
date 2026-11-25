using System.Security.Cryptography;
using System.Text;

namespace PCasa.App.Services;

// Shared cache identity and atomic publication; frame capture is supplied
// by a host codec adapter. No Windows Shell or WPF types cross the boundary.
public static class VideoPreviewCache
{
    public static string Resolve(string path,
        Action<string, string, bool, CancellationToken> encode,
        CancellationToken token = default, bool forceRefresh = false,
        string? cacheRoot = null)
    {
        if (!VideoFormats.IsVideo(path)) return path;
        token.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        var key = $"{Path.GetFullPath(path)}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var root = Path.Combine(cacheRoot ?? Path.Combine(PcasaDataPaths.SharedDataRoot, "video-previews"), hash[..2]);
        var target = Path.Combine(root, hash + ".jpg");
        {
            if (!forceRefresh && File.Exists(target) && new FileInfo(target).Length > 0)
                return target;
            Directory.CreateDirectory(root);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                token.ThrowIfCancellationRequested();
                encode(path, temporary, forceRefresh, token);
                token.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: true);
                return target;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

}
