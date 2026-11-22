using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace RentNearBy.Tests.Support;

// Photo fixtures built in code (no binary files). Shared by the pipeline, upload check and profile photo tests.
public static class TestImages
{
    public static readonly Rgba32 Red = new(255, 0, 0, 255);

    // Landscape pixels 400x300 with EXIF orientation 6 (camera held upright): shown as 300x400.
    public static byte[] PortraitExif6()
    {
        using var image = new Image<Rgba32>(400, 300, new Rgba32(30, 120, 200, 255));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "TestCam");
        return Save(image, s => image.SaveAsJpeg(s));
    }

    // 200x200, fully transparent except a blue centre square.
    public static byte[] TransparentPng()
    {
        using var image = new Image<Rgba32>(200, 200, new Rgba32(0, 0, 0, 0));
        for (var y = 50; y < 150; y++)
            for (var x = 50; x < 150; x++)
                image[x, y] = new Rgba32(0, 0, 255, 255);
        return Save(image, s => image.SaveAsPng(s));
    }

    // 3 frames of 64x64: red, green, blue.
    public static byte[] AnimatedWebp()
    {
        using var image = new Image<Rgba32>(64, 64, Red);
        using (var green = new Image<Rgba32>(64, 64, new Rgba32(0, 255, 0, 255))) image.Frames.AddFrame(green.Frames.RootFrame);
        using (var blue = new Image<Rgba32>(64, 64, new Rgba32(0, 0, 255, 255))) image.Frames.AddFrame(blue.Frames.RootFrame);
        return Save(image, s => image.SaveAsWebp(s, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }));
    }

    public static byte[] Wide() => Jpeg(2000, 1000);
    public static byte[] Tall() => Jpeg(1000, 2000);
    public static byte[] Small() => Jpeg(300, 200);

    // Random pixels: hard to compress, drives the quality loop.
    public static byte[] Noisy(int side = 1280)
    {
        var random = new Random(42);
        using var image = new Image<Rgb24>(side, side);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgb24((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        });
        return Save(image, s => image.SaveAsPng(s));
    }

    // A valid PNG header that declares 10000x5000 (50 MP) and no pixel data.
    public static byte[] HugeHeaderPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), 10000);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 5000);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    public static byte[] Empty() => [];

    public static byte[] Bmp()
    {
        using var image = new Image<Rgba32>(20, 20, Red);
        return Save(image, s => image.SaveAsBmp(s));
    }

    public static byte[] Gif()
    {
        using var image = new Image<Rgba32>(20, 20, Red);
        return Save(image, s => image.SaveAsGif(s));
    }

    public static byte[] Png() => Save(new Image<Rgba32>(20, 20, Red), (img, s) => img.SaveAsPng(s));
    public static byte[] Webp() => Save(new Image<Rgba32>(20, 20, Red), (img, s) => img.SaveAsWebp(s));
    public static byte[] SmallJpeg() => Jpeg(20, 20);

    // ISO BMFF header of a HEIC file.
    public static byte[] Heic() => [0, 0, 0, 0x18, .. "ftypheic"u8.ToArray(), 0, 0, 0, 0, .. "mif1heic"u8.ToArray()];

    public static byte[] Garbage()
    {
        var bytes = new byte[256];
        new Random(7).NextBytes(bytes);
        bytes[0] = 0x00;
        return bytes;
    }

    private static byte[] Jpeg(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h, new Rgba32(200, 150, 60, 255));
        return Save(image, s => image.SaveAsJpeg(s));
    }

    private static byte[] Save(Image image, Action<Stream> save)
    {
        using var ms = new MemoryStream();
        save(ms);
        return ms.ToArray();
    }

    private static byte[] Save(Image<Rgba32> image, Action<Image<Rgba32>, Stream> save)
    {
        using var _ = image;
        using var ms = new MemoryStream();
        save(image, ms);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        s.Write(crc);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
