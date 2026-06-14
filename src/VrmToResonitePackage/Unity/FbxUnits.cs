using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VrmToResonitePackage.Unity;

/// <summary>Reads the FBX file-unit scale used by Unity's ModelImporter "Convert Units" option.</summary>
internal static class FbxUnits
{
    private static readonly byte[] BinaryHeader = Encoding.ASCII.GetBytes("Kaydara FBX Binary");
    private static readonly byte[] UnitScaleFactor = Encoding.ASCII.GetBytes("UnitScaleFactor");

    /// <summary>Returns meters per FBX file unit, or 1 when the unit metadata cannot be read.</summary>
    public static float MetersPerUnit(string path)
    {
        const int maxHeaderBytes = 1024 * 1024;
        using FileStream file = File.OpenRead(path);
        byte[] data = new byte[Math.Min(file.Length, maxHeaderBytes)];
        file.ReadExactly(data);
        double centimeters = StartsWith(data, BinaryHeader)
            ? ReadBinaryUnitScale(data)
            : ReadAsciiUnitScale(data);
        return double.IsFinite(centimeters) && centimeters > 0
            ? (float)(centimeters / 100.0)
            : 1f;
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
