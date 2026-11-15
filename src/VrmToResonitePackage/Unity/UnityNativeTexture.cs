using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VrmToResonitePackage.Unity;

internal static class UnityNativeTexture
{
    public static void WritePng(YamlNode texture, string path)
    {
        int width = texture["m_Width"]?.AsInt() ?? 0, height = texture["m_Height"]?.AsInt() ?? 0;
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
            throw new InvalidDataException("Invalid Unity Texture2D dimensions.");
        if ((texture["m_StreamData"]?["size"]?.AsLong() ?? 0) != 0)
            throw new InvalidDataException("Streamed Unity Texture2D assets are not supported.");
        int format = texture["m_TextureFormat"]?.AsInt() ?? 0;
        int size = format switch { 1 or 63 => 1, 3 => 3, 4 or 5 or 14 => 4,
            _ => throw new InvalidDataException($"Unsupported Unity Texture2D format {format}; save an uncompressed RGBA32 texture or PNG.") };
        var source = UnityNativeMesh.Buffer(texture["image data"] ?? texture["_typelessdata"]);
        if ((long)width * height * size > source.Length) throw new InvalidDataException("Truncated Unity Texture2D pixels.");
        byte[] bgra = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int input = (y * width + x) * size, output = ((height - y - 1) * width + x) * 4;
            byte r, g, b, a = 255;
            switch (format)
            {
                case 1: r = g = b = 255; a = source[input]; break;
                case 63: r = source[input]; g = b = 0; break;
                case 5: a = source[input]; r = source[input + 1]; g = source[input + 2]; b = source[input + 3]; break;
                case 14: b = source[input]; g = source[input + 1]; r = source[input + 2]; a = source[input + 3]; break;
                default: r = source[input]; g = source[input + 1]; b = source[input + 2]; if (size == 4) a = source[input + 3]; break;
            }
            bgra[output] = b; bgra[output + 1] = g; bgra[output + 2] = r; bgra[output + 3] = a;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
