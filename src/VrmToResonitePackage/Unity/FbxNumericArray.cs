using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VrmToResonitePackage.Unity;

internal static class FbxNumericArray
{
    // FBX numeric arrays and the supported Windows x64 target are little-endian.
    public static T[] Decode<T>(ReadOnlySpan<byte> stored, int count, int encoding) where T : unmanaged
    {
        int size = checked(count * Unsafe.SizeOf<T>());
        if (count < 0 || encoding is not (0 or 1) || (encoding == 0 && stored.Length != size))
            throw new InvalidDataException("Invalid FBX numeric array header.");
        if (encoding == 0)
            return MemoryMarshal.Cast<byte, T>(stored).ToArray();

        var result = new T[count];
        // Give the decoder space to consume the zlib trailer even for an empty array.
        Span<byte> destination = size == 0 ? stackalloc byte[1] : MemoryMarshal.AsBytes(result.AsSpan());
        using var decoder = new ZLibDecoder();
        var status = decoder.Decompress(stored, destination, out int consumed, out int written);
        if (status != OperationStatus.Done || consumed != stored.Length || written != size)
            throw new InvalidDataException("Invalid compressed FBX numeric array.");
        return result;
    }
}
