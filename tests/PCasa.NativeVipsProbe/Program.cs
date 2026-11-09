using NetVips;

try
{
    // Test through the same managed binding used by PCasa, loading the native
    // DLLs from this test output, not the package's original static DLL.
    using var image = Image.Black(64, 48);
    foreach (var format in new[] { ".png", ".jpg", ".webp", ".tif" })
    {
        var encoded = image.WriteToBuffer(format);
        using var decoded = Image.NewFromBuffer(encoded);
        if (decoded.Width != 64 || decoded.Height != 48)
            throw new Exception($"Decoded dimensions mismatch for {format}");
        Console.WriteLine($"PASS {format} bytes={encoded.Length}");
    }
    Console.WriteLine("NETVIPS NATIVE DYNAMIC PROBE PASS");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("NETVIPS NATIVE DYNAMIC PROBE FAIL: " + ex);
    return 1;
}
