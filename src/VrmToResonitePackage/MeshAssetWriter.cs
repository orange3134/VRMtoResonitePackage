using Elements.Assets;
using FrooxEngine.Store;
using LZ4;

namespace VrmToResonitePackage;

/// <summary>Lossless mesh saves without MeshX's expensive high-compression pass.</summary>
internal static class MeshAssetWriter
{
    // MeshX.Decode supports an outer LZ4 stream containing a complete Plain MeshX.
    // Let MeshX write its own versioned header/payload; do not duplicate its binary schema.
    // Streaming keeps memory bounded even with thousands of blendshape frames.
    internal static void SaveToFile(MeshX mesh, string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var compressed = new LZ4Stream(file, LZ4StreamMode.Compress, LZ4StreamFlags.None);
        mesh.Encode(compressed, MeshX.Encoding.Plain);
    }

    /// <summary>Call on the background thread, like LocalDB.SaveAssetAsync(MeshX).</summary>
    internal static async Task<Uri> SaveAsync(LocalDB database, MeshX mesh)
    {
        string path = database.GetTempFilePath("meshx");
        try
        {
            SaveToFile(mesh, path);
            return await database.ImportLocalAssetAsync(path, LocalDB.ImportLocation.Move).ConfigureAwait(false);
        }
        finally
        {
            // Import normally moves the file. Failed encodes/imports must not retain a partial mesh.
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
