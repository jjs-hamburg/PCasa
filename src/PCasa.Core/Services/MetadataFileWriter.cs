using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCasa.App.Services;

/// <summary>
/// Common transport for lossless JPEG metadata updates on SMB. Native writes
/// are completed on local scratch; semantic and JPEG integrity checks happen
/// before publishing. No Undo, no sidecar and no retry through a weaker path.
/// </summary>
internal static class MetadataFileWriter
{
    private sealed record Picture(byte[] Tail, string[] ProtectedApp, string[] MpfEntries);
    private static long _samples;

    internal static ImageExifData WriteStagedJpeg(string source, IReadOnlyList<string> commands,
        ExifToolKeywordWriter.Session session, Action<string> edit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = Path.GetFullPath(source);
        var info = new FileInfo(source);
        long size = info.Length;
        var modified = info.LastWriteTimeUtc;
        var created = info.CreationTimeUtc;
        var local = Path.Combine(Path.GetTempPath(), "PCasa-metadata-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        var stage = Path.Combine(Path.GetDirectoryName(source)!, ".pcasa-metadata-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        var timer = Stopwatch.StartNew();
        long download = 0, native = 0, validation = 0, upload = 0, transport = 0;
        try
        {
            Copy(source, local, token);
            download = timer.ElapsedMilliseconds;
            var beforePicture = Inspect(local);
            using var readerLease = MetadataWriteScope.RentReader();
            var reader = readerLease.Value;
            var before = reader.Read(local, token);
            var beforeTags = session.ReadSnapshot(local, token);
            token.ThrowIfCancellationRequested();
            edit(local);
            native = timer.ElapsedMilliseconds - download;
            var after = reader.Read(local, token);
            var afterTags = session.ReadSnapshot(local, token);
            var afterPicture = Inspect(local);
            if (!beforePicture.Tail.SequenceEqual(afterPicture.Tail) ||
                !beforePicture.ProtectedApp.SequenceEqual(afterPicture.ProtectedApp) ||
                !beforePicture.MpfEntries.SequenceEqual(afterPicture.MpfEntries))
                throw new InvalidDataException("JPEG picture data or protected APP/MPF structure changed.");
            Verify(commands, before, after, beforeTags, afterTags);
            validation = timer.ElapsedMilliseconds - download - native;
            token.ThrowIfCancellationRequested();
            Copy(local, stage, token);
            upload = timer.ElapsedMilliseconds - download - native - validation;
            // One sequential remote read before the commit; no remote ExifTool
            // audit after publishing. Same bytes as the verified local candidate.
            if (!Hash(local).SequenceEqual(Hash(stage)))
                throw new IOException("Uploaded metadata candidate failed SHA-256 verification.");
            transport = timer.ElapsedMilliseconds - download - native - validation - upload;
            File.SetCreationTimeUtc(stage, created);
            File.SetLastWriteTimeUtc(stage, modified);
            for (var retry = 0;; retry++)
            {
                token.ThrowIfCancellationRequested();
                var current = new FileInfo(source);
                if (current.Length != size || current.LastWriteTimeUtc != modified)
                    throw new IOException("Metadata original changed before atomic replacement.");
                try { File.Replace(stage, source, null); break; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 1175 or 1176 or 1177 && retry < 16)
                {
                    if (token.WaitHandle.WaitOne(Math.Min(400, 100 + retry * 35)))
                        token.ThrowIfCancellationRequested();
                }
            }
            // ExifTool's -P semantics: keep source identity timestamps.
            // Once published do not observe a scheduler cancellation until
            // the caller has confirmed the catalog transaction.
            File.SetCreationTimeUtc(source, created);
            File.SetLastWriteTimeUtc(source, modified);
            var obj = JsonNode.Parse(after.Json)!.AsObject();
            obj["SourceFile"] = source;
            var verified = after with { Json = obj.ToJsonString() };
            var ordinal = Interlocked.Increment(ref _samples);
            if (ordinal <= 8 || ordinal % 25 == 0)
                PCasaDiagnostics.WriteMessage("Metadata.Stage.Profile",
                    $"DownloadMs={download}; LocalReadEditMs={native}; VerifyMs={validation}; UploadMs={upload}; TransportVerifyMs={transport}; TotalMs={timer.ElapsedMilliseconds}; File={Path.GetFileName(source)}");
            return verified;
        }
        finally
        {
            foreach (var path in new[] {local,stage})
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { PCasaDiagnostics.Write("Metadata.Stage.Cleanup", ex); }
        }
    }

    private static byte[] Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024*1024, FileOptions.SequentialScan);
        return SHA256.HashData(stream);
    }

    private static void Copy(string source, string destination, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024*1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 1024*1024, FileOptions.SequentialScan);
        var buffer = new byte[1024*1024];
        int count;
        while ((count = input.Read(buffer)) != 0)
        { token.ThrowIfCancellationRequested(); output.Write(buffer,0,count); }
        output.Flush(true);
        token.ThrowIfCancellationRequested();
    }

    private static Dictionary<string,string> Fields(ImageExifData data)
    {
        using var doc = JsonDocument.Parse(data.Json);
        return doc.RootElement.EnumerateObject().Where(p => p.Name != "SourceFile")
            .ToDictionary(p=>p.Name,p=>p.Value.ValueKind == JsonValueKind.String
                ? p.Value.GetString()! : p.Value.GetRawText(),StringComparer.Ordinal);
    }

    internal static int Orientation(ImageExifData data) =>
        Fields(data).TryGetValue("IFD0:Orientation", out var raw) &&
        int.TryParse(raw, out var n) && n is >= 1 and <= 8 ? n : 1;

    private static void Verify(IReadOnlyList<string> commands, ImageExifData before,
        ImageExifData after, ExifToolKeywordWriter.Session.KeywordSnapshot beforeTags,
        ExifToolKeywordWriter.Session.KeywordSnapshot afterTags)
    {
        var old = Fields(before);
        var next = Fields(after);
        var requested = new Dictionary<string,List<string>>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            int at = command.IndexOf('=');
            if (at <= 1) throw new InvalidDataException("Unexpected metadata command.");
            string key = command[1..at].TrimEnd('#');
            if (!requested.TryGetValue(key,out var values)) requested[key] = values = new();
            values.Add(command[(at+1)..]);
        }
        bool tags = requested.ContainsKey("XMP-dc:Subject") || requested.ContainsKey("IPTC:Keywords");
        bool gps = requested.Keys.Any(k => k.StartsWith("EXIF:GPS", StringComparison.Ordinal) ||
            k.StartsWith("XMP-exif:GPS", StringComparison.Ordinal));
        bool orientation = requested.ContainsKey("IFD0:Orientation");
        var dates = ProtectedExifDateTags.ProtectedTags.Where(t => requested.ContainsKey(t.WriteTag))
            .ToDictionary(t=>t.ReadTag,t=>requested[t.WriteTag].Single());
        static bool SetEquals(IEnumerable<string> a,IEnumerable<string> b) =>
            a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);
        if (!SetEquals(afterTags.Xmp, tags ? requested.GetValueOrDefault("XMP-dc:Subject",new()).Where(s=>s.Length>0) : beforeTags.Xmp) ||
            !SetEquals(afterTags.Iptc, tags ? requested.GetValueOrDefault("IPTC:Keywords",new()).Where(s=>s.Length>0) : beforeTags.Iptc))
            throw new InvalidDataException("JPEG keyword verification failed.");
        if (orientation && Orientation(after) != int.Parse(requested["IFD0:Orientation"].Single(),CultureInfo.InvariantCulture))
            throw new InvalidDataException("JPEG orientation verification failed.");
        if (gps)
        {
            double? Coordinate(string name, string reference, string negative)
            {
                if (!requested.TryGetValue("EXIF:"+name,out var values) ||
                    !double.TryParse(values.Single(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return null;
                return requested.TryGetValue("EXIF:"+reference,out var sign) && sign.Single()==negative ? -value : value;
            }
            static bool Same(double? a,double? b) => a is null ? b is null :
                b is double x && Math.Abs(a.Value-x)<.0001;
            if (!Same(Coordinate("GPSLatitude","GPSLatitudeRef","S"),after.GpsLatitude) ||
                !Same(Coordinate("GPSLongitude","GPSLongitudeRef","W"),after.GpsLongitude))
                throw new InvalidDataException("JPEG GPS verification failed.");
        }
        foreach (var (key,value) in dates)
            if ((next.GetValueOrDefault(key) ?? "") != value)
                throw new InvalidDataException("Requested JPEG date not confirmed: "+key);
        // Preserve all original EXIF fields except the specific requested
        // change and structural offsets which ExifTool must relocate.
        foreach (var (key,value) in old)
        {
            if (dates.ContainsKey(key) || orientation && key=="IFD0:Orientation" ||
                gps && (key.StartsWith("GPS:",StringComparison.Ordinal) || key.StartsWith("XMP-exif:GPS",StringComparison.Ordinal)) ||
                key.EndsWith(":ThumbnailOffset",StringComparison.Ordinal) ||
                key.EndsWith(":ThumbnailLength",StringComparison.Ordinal) ||
                key.EndsWith(":PreviewImageStart",StringComparison.Ordinal) ||
                key.EndsWith(":PreviewImageLength",StringComparison.Ordinal)) continue;
            if (next.GetValueOrDefault(key) != value)
                throw new InvalidDataException("Unrequested JPEG metadata changed: "+key);
        }
        foreach (var (_,readTag) in ProtectedExifDateTags.ProtectedTags)
            if (!dates.ContainsKey(readTag) && old.GetValueOrDefault(readTag) != next.GetValueOrDefault(readTag))
                throw new InvalidDataException("Protected JPEG date changed: "+readTag);
    }

    // Parse the APP prefix only; hash the unchanged compressed scan and every
    // trailing embedded image. MPF offsets themselves legitimately move when
    // metadata length changes, so validate the referenced entries rather than
    // comparing raw MPF offsets.
    private static Picture Inspect(string path)
    {
        using var input = File.OpenRead(path);
        if(input.ReadByte()!=255 || input.ReadByte()!=216)
            throw new InvalidDataException("Missing JPEG SOI.");
        var protectedApp = new List<string>();
        var mpf = new List<string>();
        bool foundMpf = false;
        for(int count=0;count<512 && input.Position<16*1024*1024;count++)
        {
            long markerStart=input.Position;
            if(input.ReadByte()!=255) break;
            int marker; do {marker=input.ReadByte();} while(marker==255);
            if(marker==218)
            {
                input.Position=markerStart;
                return new Picture(SHA256.HashData(input),protectedApp.Order().ToArray(),mpf.ToArray());
            }
            int hi=input.ReadByte(),lo=input.ReadByte();
            int len=(hi<<8)+lo-2;
            if(marker<=0 || hi<0 || lo<0 || len<0 || input.Position+len>input.Length)
                break;
            long pos=input.Position;
            var chunk=new byte[len]; input.ReadExactly(chunk);
            if(marker==226 && chunk.AsSpan().StartsWith("MPF\0"u8))
            {
                if(foundMpf) throw new InvalidDataException("Multiple MPF indexes.");
                foundMpf=true;
                mpf.AddRange(MpfEntries(input,chunk,pos+4));
            }
            else if(!(marker==225 && (chunk.AsSpan().StartsWith("Exif\0\0"u8) ||
                 chunk.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/"u8) ||
                 chunk.AsSpan().StartsWith("http://ns.adobe.com/xmp/extension/"u8))) &&
                 !(marker==237 && chunk.AsSpan().StartsWith("Photoshop 3.0\0"u8)))
                protectedApp.Add(marker.ToString("X2")+":"+Convert.ToHexString(SHA256.HashData(chunk)));
        }
        throw new InvalidDataException("JPEG header is invalid or unsupported.");
    }

    private static IEnumerable<string> MpfEntries(FileStream stream,byte[] bytes,long tiffStart)
    {
        var data=bytes.AsSpan(4);
        bool le=data.StartsWith("II"u8);
        if(data.Length<8 || !le&&!data.StartsWith("MM"u8))
            throw new InvalidDataException("Invalid MPF byte order.");
        ushort U16(ReadOnlySpan<byte> s)=>le?BinaryPrimitives.ReadUInt16LittleEndian(s):BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s)=>le?BinaryPrimitives.ReadUInt32LittleEndian(s):BinaryPrimitives.ReadUInt32BigEndian(s);
        if(U16(data[2..])!=42) throw new InvalidDataException("Invalid MPF TIFF.");
        int offset=checked((int)U32(data[4..]));
        if(offset<8 || offset>data.Length-2) throw new InvalidDataException("Invalid MPF IFD.");
        int count=U16(data[offset..]);
        if(count>256 || offset+2+12L*count+4>data.Length) throw new InvalidDataException("Invalid MPF IFD.");
        int images=0,entryOffset=0,entryBytes=0;
        for(int i=0;i<count;i++)
        {
            var entry=data.Slice(offset+2+i*12,12);
            switch(U16(entry))
            {
                case 0xb001: images=checked((int)U32(entry[8..]));break;
                case 0xb002: entryBytes=checked((int)U32(entry[4..]));entryOffset=checked((int)U32(entry[8..]));break;
            }
        }
        if(images<2 || images>128 || entryBytes!=16*images ||
            entryOffset<0 || entryOffset>data.Length-entryBytes)
            throw new InvalidDataException("Unsupported MPF index.");
        var result=new List<string>();
        long restore=stream.Position;
        for(int i=0;i<images;i++)
        {
            var e=data.Slice(entryOffset+16*i,16);
            uint attribute=U32(e),size=U32(e[4..]),relative=U32(e[8..]);
            uint dependencies=U32(e[12..]);
            if(i==0)
            {
                if(relative!=0 || size<4 || size>stream.Length)
                    throw new InvalidDataException("Invalid primary MPF entry.");
                result.Add($"{attribute:X8}:{dependencies:X8}");
                continue;
            }
            long start=tiffStart+relative;
            if(relative==0 || size<4 || start<restore || start>stream.Length-size)
                throw new InvalidDataException("Invalid secondary MPF entry.");
            stream.Position=start;
            if(stream.ReadByte()!=255 || stream.ReadByte()!=216)
                throw new InvalidDataException("MPF entry does not reference JPEG SOI.");
            stream.Position=start+size-2;
            if(stream.ReadByte()!=255 || stream.ReadByte()!=217)
                throw new InvalidDataException("MPF entry does not reference JPEG EOI.");
            // Tail hash includes secondary bytes; relative-to-end position is
            // invariant to relocation of APP headers.
            result.Add($"{attribute:X8}:{dependencies:X8}:{size}:{stream.Length-start}");
        }
        stream.Position=restore;
        return result;
    }
}
