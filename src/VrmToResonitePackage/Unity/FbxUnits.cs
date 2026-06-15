using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VrmToResonitePackage.Unity;

/// <summary>Reads the FBX file-unit scale used by Unity's ModelImporter "Convert Units" option.</summary>
internal static class FbxUnits
{
    private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary");
    private static readonly byte[] UnitScaleFactor = Encoding.ASCII.GetBytes("UnitScaleFactor");
    private static readonly byte[] UpAxis = Encoding.ASCII.GetBytes("UpAxis");
    private static readonly byte[] UpAxisSign = Encoding.ASCII.GetBytes("UpAxisSign");

    /// <summary>Returns meters per FBX file unit, or 1 when the unit metadata cannot be read.</summary>
    public static float MetersPerUnit(string path)
    {
        byte[] data = ReadHeader(path);
        double centimeters = StartsWith(data, BinaryHeader)
            ? ReadBinaryUnitScale(data)
            : ReadAsciiUnitScale(data);
        return double.IsFinite(centimeters) && centimeters > 0
            ? (float)(centimeters / 100.0)
            : 1f;
    }

    /// <summary>Returns the FBX file's native up axis for diagnostics.</summary>
    public static string UpAxisDescription(string path)
    {
        byte[] data = ReadHeader(path);
        bool binary = StartsWith(data, BinaryHeader);
        int axis = binary ? ReadBinaryInt(data, UpAxis) : ReadAsciiInt(data, "UpAxis");
        int sign = binary ? ReadBinaryInt(data, UpAxisSign) : ReadAsciiInt(data, "UpAxisSign");
        if (sign is not (-1 or 1))
        {
            sign = 1;
        }

        return axis switch
        {
            0 => sign > 0 ? "X+" : "X-",
            1 => sign > 0 ? "Y+" : "Y-",
            2 => sign > 0 ? "Z+" : "Z-",
            _ => "unknown",
        };
    }

    private static byte[] ReadHeader(string path)
    {
        const int maxHeaderBytes = 1024 * 1024;
        using FileStream file = File.OpenRead(path);
        byte[] data = new byte[Math.Min(file.Length, maxHeaderBytes)];
        file.ReadExactly(data);
        return data;
    }

    private static double ReadBinaryUnitScale(byte[] data)
    {
        int name = IndexOf(data, UnitScaleFactor);
        if (name < 0)
        {
            return double.NaN;
        }
        int end = Math.Min(data.Length - sizeof(double) - 1, name + UnitScaleFactor.Length + 128);
        for (int i = name + UnitScaleFactor.Length; i <= end; i++)
        {
            if (data[i] == (byte)'D')
            {
                return BitConverter.ToDouble(data, i + 1);
            }
        }
        return double.NaN;
    }

    private static double ReadAsciiUnitScale(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        Match match = Regex.Match(text,
            @"UnitScaleFactor[^-\d+]*([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)",
            RegexOptions.CultureInvariant);
        return match.Success &&
               double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : double.NaN;
    }

    private static int ReadBinaryInt(byte[] data, byte[] propertyName)
    {
        int name = IndexOf(data, propertyName);
        if (name < 0)
        {
            return int.MinValue;
        }
        int end = Math.Min(data.Length - sizeof(int) - 1, name + propertyName.Length + 128);
        for (int i = name + propertyName.Length; i <= end; i++)
        {
            if (data[i] == (byte)'I')
            {
                int value = BitConverter.ToInt32(data, i + 1);
                if (value is >= -1 and <= 2)
                {
                    return value;
                }
            }
        }
        return int.MinValue;
    }

    private static int ReadAsciiInt(byte[] data, string propertyName)
    {
        string text = Encoding.UTF8.GetString(data);
        Match match = Regex.Match(text, $@"{propertyName}[^-\d+]*([-+]?\d+)", RegexOptions.CultureInvariant);
        return match.Success &&
               int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : int.MinValue;
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i]) return false;
        }
        return true;
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
