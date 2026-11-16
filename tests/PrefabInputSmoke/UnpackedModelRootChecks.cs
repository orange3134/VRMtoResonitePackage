using System.Reflection;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class UnpackedModelRootChecks
{
    public static void Run(Func<string, string, string, string> asset, string fbx, string prefab, string sourceGuid)
    {
        const string modelGuid = "bb736737373737373737373737373737";
        foreach (string scenario in new[] { "identity", "translated", "sibling", "nested" })
        {
            string container = scenario == "translated"
                ? "Properties70: {\n P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,2,0\n }"
                : "";
            string wrapped = fbx.Replace("Objects: {", "Objects: {\n Model: 90, \"Model::Container\", \"Null\" { " + container + " }")
                .Replace("\"OO\",1,0", "\"OO\",1,90").Replace("\"OO\",2,0", "\"OO\",2,90")
                .Replace("\"OO\",5,0", "\"OO\",5,90")
                .Replace("Connections: {", "Connections: {\n C: \"OO\",90,0");
            if (scenario == "sibling") wrapped = wrapped.Replace("\"OO\",5,90", "\"OO\",5,0");
            if (scenario == "nested") wrapped = wrapped
                .Replace("Objects: {", "Objects: {\n Model: 91, \"Model::Extra\", \"Null\" { }")
                .Replace("\"OO\",1,90", "\"OO\",1,91")
                .Replace("Connections: {", "Connections: {\n C: \"OO\",91,90");
            string model = asset("Assets/UnpackedContainer.fbx", modelGuid, wrapped);
            File.AppendAllText(model + ".meta", "ModelImporter:\n  internalIDToNameTable:\n  - first:\n      43: 4300000\n    second: Body\n");
            string input = asset("Assets/UnpackedContainer.prefab", "bb747474747474747474747474747474",
                prefab.Replace(sourceGuid, modelGuid));
            using var package = UnityPackage.Open(input);
            var avatar = new VrchatAvatar { FbxGuid = modelGuid };
            typeof(VrchatAvatarParser).GetMethod("ParsePhysics", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { package, package.InputPrefab.Guid, avatar, null });
            string[] paths = avatar.AuthoredBoneTransforms.Keys.Select(t => t.Path).Order().ToArray();
            string[] expected = scenario == "identity" ? new[] { "Container/Left", "Container/Left/Shared" } : Array.Empty<string>();
            if (!paths.SequenceEqual(expected))
                throw new Exception($"Unpacked root {scenario}: incorrect skeleton paths: {string.Join(", ", paths)}");
            if (scenario == "identity" && avatar.AuthoredBoneTransforms.Single(p => p.Key.Name == "Shared").Value.Position !=
                new System.Numerics.Vector3(.12f, .34f, -.56f))
                throw new Exception("Unpacked model container lost authored local bone pose.");
        }
        Console.WriteLine("PASS: unpacked identity model container retains full bone paths; transformed, sibling and nested branches stay distinct");
    }
}
