using System.Security.Cryptography;
using System.Text.Json;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class ViewpointSnapshot
{
    public static void Write(string input, string output, string selection)
    {
        input = Path.GetFullPath(input);
        if (File.Exists(output)) throw new IOException("Snapshot output already exists.");
        // Keep a read-only handle open through parsing so the hashed input cannot
        // be replaced or modified while its settings are being captured.
        using var inputFile = File.OpenRead(input);
        string inputHash = Convert.ToHexStringLower(SHA256.HashData(inputFile));
        using var source = UnityPackage.Open(input);
        var avatar = VrchatAvatarParser.Parse(source, selection);
        var snapshot = new
        {
            format = "resopon.parser-avatar-viewpoint", schemaVersion = 1,
            inputPath = input, inputSha256 = inputHash,
            prefabPath = avatar.PrefabPath,
            prefabGuid = source.Assets.Values.Single(a => a.LogicalPath == avatar.PrefabPath).Guid,
            descriptorRootKey = avatar.DescriptorRootKey,
            viewPosition = avatar.ViewPosition is {} p ? new[] { p.X, p.Y, p.Z } : null,
        };
        using var file = new FileStream(output, FileMode.CreateNew);
        JsonSerializer.Serialize(file, snapshot, new JsonSerializerOptions { WriteIndented = true });
    }
}
