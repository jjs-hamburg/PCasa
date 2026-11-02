using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCasa.App.Services;

/// <summary>
/// Test-only Windows SMB CopyChunk candidate, no NAS vendor dependencies.
/// Copies a verified unchanged JPEG tail between two files on one root.
/// When unsupported or incomplete it returns false; the caller MUST discard
/// the partial target tail and copy from the original by normal streaming.
/// This class is not wired into the production metadata writer.
/// </summary>
internal static class JpegSmbCopyChunkPrototype
{
    // Microsoft FSCTL_SRV_REQUEST_RESUME_KEY / FSCTL_SRV_COPYCHUNK.
    private const uint RequestKey = 0x00140078;
    private const uint CopyChunk = 0x001440F2;
    private const int ChunkBytes = 1024 * 1024;
    private const int MaxChunksPerRequest = 16; // 16 MiB total
    private const int CopyChunkHeaderBytes = 32;
    private const int CopyChunkEntryBytes = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint control,
        [In] byte[]? input, uint inputLength,
        [Out] byte[] output, uint outputLength,
        out uint bytesReturned, IntPtr overlapped);

    public static bool TryCopyUnchangedTail(FileStream source, FileStream target,
        long sourceOffset, long destinationOffset, long length,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || length < 1 ||
            sourceOffset < 0 || destinationOffset < 0)
            return false;
        token.ThrowIfCancellationRequested();

        // Server-side copies require two handles to the same share. No
        // remote probing, credentials or separate server installation.
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(source.Name));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(target.Name));
        if (string.IsNullOrEmpty(sourceRoot) ||
            !string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        source.Flush();
        target.Flush();

        var resume = new byte[32];
        if (!DeviceIoControl(source.SafeFileHandle, RequestKey, null, 0,
            resume, (uint)resume.Length, out var keyLength,
            IntPtr.Zero) || keyLength < 24)
            return false;

        long completed = 0;
        while (completed < length)
        {
            token.ThrowIfCancellationRequested();
            var remaining = length - completed;
            var count = (int)Math.Min(MaxChunksPerRequest,
                (remaining + ChunkBytes - 1) / ChunkBytes);
            var request = new byte[CopyChunkHeaderBytes +
                count * CopyChunkEntryBytes];
            Buffer.BlockCopy(resume, 0, request, 0, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(24, 4),
                (uint)count);
            long expected = 0;
            for (var i = 0; i < count; i++)
            {
                var part = (int)Math.Min(ChunkBytes,
                    remaining - (long)i * ChunkBytes);
                var offset = CopyChunkHeaderBytes + i * CopyChunkEntryBytes;
                BinaryPrimitives.WriteInt64LittleEndian(
                    request.AsSpan(offset, 8),
                    checked(sourceOffset + completed + expected));
                BinaryPrimitives.WriteInt64LittleEndian(
                    request.AsSpan(offset + 8, 8),
                    checked(destinationOffset + completed + expected));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    request.AsSpan(offset + 16, 4), (uint)part);
                expected += part;
            }
            var response = new byte[12];
            if (!DeviceIoControl(target.SafeFileHandle, CopyChunk, request,
                (uint)request.Length, response, (uint)response.Length,
                out var responseLength, IntPtr.Zero) ||
                responseLength < response.Length)
                return false;
            var chunksWritten = BinaryPrimitives.ReadUInt32LittleEndian(
                response.AsSpan(0, 4));
            var bytesWritten = BinaryPrimitives.ReadUInt32LittleEndian(
                response.AsSpan(8, 4));
            if (chunksWritten != count || bytesWritten != expected)
                return false;
            completed += expected;
        }
        return true;
    }
}
