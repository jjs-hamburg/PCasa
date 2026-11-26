using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCasa.App.Services;

internal static class Program
{
    private record Sample(string File, long Bytes, string Operation, string Method,
        double Read, double Download, double Edit, double Build, double Verify,
        double Upload, double Swap, double Total, double Audit, bool Offload,
        string Status, string Detail);

    private sealed record State(Dictionary<string,string> Exif,
        ExifToolKeywordWriter.Session.KeywordSnapshot Tags, byte[] Pixels,
        double? Latitude, double? Longitude);

    private static readonly string[] Operations = ["Rotation", "Datum", "Tags", "GPS"];
    private static readonly string[] Methods = ["A-Direkt", "B-Lokal", "C-Header"];

    [STAThread]
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        Console.OutputEncoding = Encoding.UTF8;
        bool selfTest = args.Length == 2 && args[0] == "--self-test";
        if ((!selfTest && args.Length != 1) || !Directory.Exists(args[^1])) return 2;
        string root = Path.GetFullPath(args[^1]);
        string local = Path.Combine(Path.GetTempPath(), "PCasa-bench-data-" + Guid.NewGuid().ToString("N"));
        string remote = Path.Combine(root, ".pcasa-benchmark-" + Guid.NewGuid().ToString("N"));
        string report = Path.Combine(root, "Benchmark-Ergebnis.txt");
        Directory.CreateDirectory(local);
        Environment.SetEnvironmentVariable("PCASA_BENCHMARK_LOG", Path.Combine(local, "diagnostics.log"));
        Environment.SetEnvironmentVariable("PCASA_EXIFTOOL_PATH",
            Path.Combine(AppContext.BaseDirectory, "ExifTool", "exiftool.exe"));
        Environment.SetEnvironmentVariable("TEMP", local);
        Environment.SetEnvironmentVariable("TMP", local);
        var rows = new List<Sample>();
        var failures = 0;
        using var log = new StreamWriter(report, false, new UTF8Encoding(true)) { AutoFlush = true };
        void Print(string value) { Console.WriteLine(value); log.WriteLine(value); }
        try
        {
            if (selfTest)
            {
                var fixture = Path.Combine(root, "test.jpg");
                var image = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgr24,
                    null, new byte[64 * 32 * 3], 64 * 3);
                var encoder = new JpegBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(fixture)) encoder.Save(stream);
                using var init = new ExifToolKeywordWriter.Session();
                init.WriteOrientation(fixture, 1, default);
                init.WriteExactExifDates(fixture, new Dictionary<string,string?> {
                    ["EXIF:DateTimeOriginal"] = "2000:01:01 12:00:00"
                });
            }
            var sources = Directory.EnumerateFiles(root)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".jpg" or ".jpeg")
                .Where(p => !Path.GetFileName(p).StartsWith(".pcasa-", StringComparison.OrdinalIgnoreCase))
                .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            Print("PCasa Metadaten-Benchmark 1 | " + DateTimeOffset.Now.ToString("O"));
            Print("Ordner: " + root + " | JPEGs: " + sources.Length);
            Print("A=ExifTool auf Testkopie; B=lokale Vollkopie; C=PCasa Header + SMB CopyChunk sofern verfuegbar.");
            Print("Vier getrennte Aenderungen, jeweils frische identische Testkopien; Vorbereitung und unabhaengige Nachpruefung separat.");
            Print("Keine Undo-Sicherung, kein PCasa-Katalog. Zeit fuer Katalog/Thumbnail wird hier NICHT gemessen.");
            Print("Zeiten in Sekunden; C-Build beinhaltet interne Tail-Integritaetspruefung. Audit ist nicht in Total enthalten.");
            if (sources.Length == 0) { Print("Keine JPEGs gefunden."); return 2; }
            Directory.CreateDirectory(remote);
            using var reader = new ImageExifReader();
            using var writer = new ExifToolKeywordWriter.Session();
            // Equal warm native processes before any measured write.
            var warm = Path.Combine(local, "warm.jpg");
            File.Copy(sources[0], warm);
            try { writer.ReadSnapshot(warm); reader.Read(warm); }
            catch (Exception ex) { Print("Warm-up: " + ex.Message); }
            File.Delete(warm);

            for (var i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                string name = Path.GetFileName(source);
                Print($"\n[{i+1}/{sources.Length}] {name}");
                try
                {
                    var original = new FileInfo(source);
                    var size = original.Length;
                    var modified = original.LastWriteTimeUtc;
                    var created = original.CreationTimeUtc;
                    byte[] fullHash = Hash(source);
                    for (int j = 0; j < Operations.Length; j++)
                    {
                        var op = Operations[(i + j) % Operations.Length];
                        for (int k = 0; k < Methods.Length; k++)
                        {
                            var method = Methods[(i + j + k) % Methods.Length];
                            var row = Trial(source, size, op, method, remote, local, writer, reader, Print);
                            rows.Add(row);
                            if (row.Status == "FAIL") failures++;
                            Print($"{row.Operation};{row.Method};{row.Status};read={row.Read:F3};download={row.Download:F3};edit={row.Edit:F3};build={row.Build:F3};verify={row.Verify:F3};upload={row.Upload:F3};swap={row.Swap:F3};total={row.Total:F3};audit={row.Audit:F3};CopyChunk={row.Offload};{row.Detail}");
                        }
                    }
                    var after = new FileInfo(source);
                    if (after.Length != size || after.LastWriteTimeUtc != modified ||
                        after.CreationTimeUtc != created || !fullHash.SequenceEqual(Hash(source)))
                        throw new IOException("Original wurde waehrend des Tests geaendert. Ergebnisse ungueltig.");
                    Print("Original SHA-256 + Dateidaten unveraendert.");
                }
                catch (Exception ex) { failures++; Print("DATEIFEHLER: " + ex.Message); }
            }
            Print("\nZUSAMMENFASSUNG (nur vollstaendig verifizierte Ergebnisse)");
            foreach (var op in Operations)
            {
                var complete = rows.Where(r => r.Operation == op).GroupBy(r => r.File)
                    .Where(g => g.Count() == 3 && g.All(r => r.Status == "PASS"))
                    .SelectMany(g => g).ToArray();
                Print(op + ": " + complete.Length / 3 + " vergleichbare Bildsaetze A/B/C.");
                foreach (var method in Methods)
                {
                    var all = rows.Where(r => r.Operation == op && r.Method == method).ToArray();
                    var good = all.Where(r => r.Status == "PASS").Select(r => r.Total).Order().ToArray();
                    var paired = complete.Where(r => r.Method == method).Select(r => r.Total).Order().ToArray();
                    Print($"{method}: PASS={good.Length}, SKIP={all.Count(r=>r.Status=="SKIP")}, FAIL={all.Count(r=>r.Status=="FAIL")}, Median alle={Median(good)}, Median gepaart={Median(paired)}");
                }
            }
            Print("Fehlgeschlagene Versuche: " + failures);
            if (selfTest && (rows.Count != 12 || rows.Any(r => r.Status != "PASS"))) return 1;
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex) { Print("ABBRUCH: " + ex); return 1; }
        finally
        {
            foreach (var path in new[] { remote, local })
                try { if (Directory.Exists(path)) Directory.Delete(path, true); }
                catch (Exception ex) { Print("BEREINIGUNG FEHLGESCHLAGEN: " + path + " | " + ex.Message); }
        }
    }

    private static string Median(double[] values) => values.Length == 0 ? "-" :
        ((values[(values.Length-1)/2] + values[values.Length/2])/2).ToString("F3") + " s";

    private static byte[] Hash(string path)
    {
        using var input = File.OpenRead(path);
        return SHA256.HashData(input);
    }

    private static State Read(string path, ImageExifReader reader, ExifToolKeywordWriter.Session writer)
    {
        var data = reader.Read(path);
        using var doc = JsonDocument.Parse(data.Json);
        return new State(doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "SourceFile").ToDictionary(p => p.Name, p => p.Value.GetRawText()),
            writer.ReadSnapshot(path), JpegGpsPatchPrototype.CompressedImageHash(path),
            data.GpsLatitude, data.GpsLongitude);
    }

    private static string? Value(State s, string key) =>
        s.Exif.TryGetValue(key, out var value) ? value : null;
    private static int Orientation(State s) => int.TryParse(Value(s, "IFD0:Orientation"), out var value) ? value : 1;
    private static bool SameTags(IEnumerable<string> a, IEnumerable<string> b) =>
        a.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private static void Verify(State before, State after, string op, int orientation,
        string date, IReadOnlyList<string> tags, double lat, double lon)
    {
        if (!before.Pixels.SequenceEqual(after.Pixels))
            throw new InvalidDataException("JPEG scan bytes changed.");
        bool Allowed(string key) => op switch
        {
            "Rotation" => key == "IFD0:Orientation",
            "Datum" => key == "ExifIFD:DateTimeOriginal",
            "GPS" => key is "GPS:GPSLatitude" or "GPS:GPSLatitudeRef" or "GPS:GPSLongitude" or
                "GPS:GPSLongitudeRef" or "XMP-exif:GPSLatitude" or "XMP-exif:GPSLongitude",
            _ => false
        };
        // Structural offsets are format bookkeeping, not semantic fields.
        // Never ignore other existing EXIF values (including MakerNotes).
        foreach (var (key, value) in before.Exif)
        {
            if (Allowed(key) || key.EndsWith(":ThumbnailOffset") || key.EndsWith(":ThumbnailLength") ||
                key.EndsWith(":PreviewImageStart") || key.EndsWith(":PreviewImageLength")) continue;
            if (Value(after,key) != value) throw new InvalidDataException("Unrequested metadata change: " + key);
        }
        foreach (var key in new[] { "ExifIFD:DateTimeOriginal", "ExifIFD:CreateDate", "IFD0:ModifyDate",
            "ExifIFD:OffsetTimeOriginal", "ExifIFD:SubSecTimeOriginal", "XMP-xmp:CreateDate",
            "XMP-xmp:ModifyDate", "XMP-exif:DateTimeOriginal", "IPTC:DateCreated", "IPTC:TimeCreated" })
            if (!Allowed(key) && Value(before,key) != Value(after,key))
                throw new InvalidDataException("Protected date changed: " + key);
        if (op == "Rotation" && Orientation(after) != orientation)
            throw new InvalidDataException("Orientation not confirmed.");
        if (op == "Datum" && Value(after,"ExifIFD:DateTimeOriginal") != JsonSerializer.Serialize(date))
            throw new InvalidDataException("Capture date not confirmed.");
        if (op == "GPS" && (after.Latitude is not double a || after.Longitude is not double b ||
            Math.Abs(a-lat) > .0001 || Math.Abs(b-lon) > .0001))
            throw new InvalidDataException("GPS not confirmed.");
        if (!SameTags(after.Tags.Xmp, op == "Tags" ? tags : before.Tags.Xmp) ||
            !SameTags(after.Tags.Iptc, op == "Tags" ? tags : before.Tags.Iptc))
            throw new InvalidDataException("Tag values changed unexpectedly.");
    }

    private static void Copy(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 131072, FileOptions.SequentialScan);
        input.CopyTo(output,131072);
        output.Flush(true);
    }

    private static Sample Trial(string source, long size, string op, string method,
        string remote, string local, ExifToolKeywordWriter.Session writer,
        ImageExifReader reader, Action<string> print)
    {
        var id = Guid.NewGuid().ToString("N");
        string target = Path.Combine(remote,id+"-test.jpg");
        string stage = Path.Combine(remote,id+"-stage.jpg");
        string work = Path.Combine(local,id+"-local.jpg");
        double read=0,down=0,edit=0,build=0,verify=0,upload=0,swap=0,total=0,audit=0;
        bool offload=false;
        string status="FAIL",detail="";
        var timer = new Stopwatch();
        double Mark(){double n=timer.Elapsed.TotalSeconds;timer.Restart();return n;}
        try
        {
            // Setup is identical for each method and deliberately excluded.
            Copy(source,target);
            var initial = new FileInfo(target);
            long ticks = initial.LastWriteTimeUtc.Ticks;
            var whole=Stopwatch.StartNew();
            timer.Start();
            string input = target;
            if(method=="B-Lokal") { Copy(target,work); down=Mark(); input=work; }
            var before=Read(input,reader,writer);
            read=Mark();
            int orientation=ExifOrientation.RotateQuarterTurns(Orientation(before),1);
            string date=Value(before,"ExifIFD:DateTimeOriginal") == JsonSerializer.Serialize("2001:02:03 04:05:06")
                ? "2002:03:04 05:06:07" : "2001:02:03 04:05:06";
            var tags=before.Tags.Combined.Append("PCasaBenchmark").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if(before.Tags.Combined.Contains("PCasaBenchmark",StringComparer.OrdinalIgnoreCase))
                tags.Add("PCasaBenchmark-2");
            double lat=53.55782,lon=10.00896;
            if(before.Latitude is double x && Math.Abs(x-lat)<.0001) lat=52.12345;
            void Edit(string path)
            {
                switch(op)
                {
                    case "Rotation": writer.WriteOrientation(path,orientation,default); break;
                    case "Datum": writer.WriteExactExifDates(path,new Dictionary<string,string?>{
                        ["EXIF:DateTimeOriginal"]=date});break;
                    case "Tags": writer.Write(path,tags);break;
                    case "GPS": writer.WriteGps(path,lat,lon);break;
                }
            }
            if(method=="C-Header")
            {
                if(!HeaderGuard.SafeBasicJpeg(target,out var reason))
                { status="SKIP"; detail=reason; return Result(); }
                var outcome=JpegTagRewritePrototype.CreateBenchmarkCopy(target,stage,writer,Edit,default);
                edit=outcome.MetadataTime.TotalSeconds;
                build=outcome.CopyTime.TotalSeconds;
                offload=outcome.ServerSideCopyUsed;
                timer.Restart();
                Verify(before,Read(stage,reader,writer),op,orientation,date,tags,lat,lon);
                verify=Mark();
            }
            else
            {
                Edit(input); edit=Mark();
                Verify(before,Read(input,reader,writer),op,orientation,date,tags,lat,lon);
                verify=Mark();
                if(method=="B-Lokal")
                {
                    Copy(work,stage); upload=Mark();
                }
            }
            if(method!="A-Direkt")
            {
                if(new FileInfo(target).Length != size || File.GetLastWriteTimeUtc(target).Ticks != ticks)
                    throw new IOException("Test target changed unexpectedly.");
                // Test copy only, never the selected source.
                File.Replace(stage,target,null);
                swap=Mark();
            }
            total=whole.Elapsed.TotalSeconds;
            timer.Restart();
            // A final independent read of the actual on-volume result, same
            // for every method. Report separately from writer wall time.
            Verify(before,Read(target,reader,writer),op,orientation,date,tags,lat,lon);
            audit=Mark();
            status="PASS";
        }
        catch(NotSupportedException ex) when(method=="C-Header")
        { status="SKIP"; detail=ex.Message; }
        catch(Exception ex){ detail=ex.GetType().Name+": "+ex.Message; }
        finally
        {
            foreach(var path in new[]{target,stage,work})
                try{if(File.Exists(path))File.Delete(path);}
                catch(Exception ex){print("TEMP CLEANUP: "+path+" | "+ex.Message);}
        }
        return Result();

        Sample Result()=>new(Path.GetFileName(source),size,op,method,read,down,edit,build,verify,upload,swap,total,audit,offload,status,detail);
    }
}

internal static class HeaderGuard
{
    internal static bool SafeBasicJpeg(string path, out string reason)
    {
        // Strict, read-only guard. Every refusal supplies a precise category;
        // never relax protection merely because a source is on slow SMB.
        reason = "Unknown";
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read);
        if (file.ReadByte() != 0xff || file.ReadByte() != 0xd8)
        {
            reason = "MissingSOI";
            return false;
        }
        for (var n = 0; n < 512 && file.Position < 16 * 1024 * 1024; n++)
        {
            var markerPrefix = file.ReadByte();
            if (markerPrefix != 0xff)
            {
                reason = markerPrefix < 0 ? "TruncatedMarker" : "UnexpectedMarkerPrefix";
                return false;
            }
            int marker;
            do { marker = file.ReadByte(); }
            while (marker == 0xff);
            if (marker == 0xda)
            {
                reason = "Supported";
                return true;
            }
            if (marker <= 0 || marker is 0xd8 or 0xd9 ||
                marker is >= 0xd0 and <= 0xd7)
            {
                reason = "InvalidMarker:" + marker.ToString("X2");
                return false;
            }
            var hi = file.ReadByte();
            var lo = file.ReadByte();
            var size = (hi << 8) + lo - 2;
            if (hi < 0 || lo < 0 || size < 0 ||
                file.Position + size > file.Length)
            {
                reason = "InvalidSegment:" + marker.ToString("X2");
                return false;
            }
            if (marker is >= 0xe1 and <= 0xef)
            {
                // MPF/MPO, Extended XMP, JUMBF and gain maps can contain
                // cross-segment offsets. Do not rebase these without
                // explicitly proving the container format is safe.
                var count = Math.Min(size, 256);
                var sample = new byte[count];
                file.ReadExactly(sample);
                if (sample.AsSpan().StartsWith("MPF\0"u8))
                {
                    reason = "MPF";
                    return false;
                }
                if (sample.AsSpan().StartsWith(
                    "http://ns.adobe.com/xmp/extension/"u8))
                {
                    reason = "ExtendedXMP";
                    return false;
                }
                if (sample.AsSpan().StartsWith("JUMBF"u8))
                {
                    reason = "JUMBF";
                    return false;
                }
                if (sample.AsSpan().StartsWith("HDRGM"u8))
                {
                    reason = "HDRGainMap";
                    return false;
                }
                file.Position += size - count;
            }
            else file.Position += size;
        }
        reason = "HeaderLimitOrNoSOS";
        return false;
    }
}
