using System.Numerics;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class HumanoidSkeletonMappingChecks
{
    private record Joint(string Name, string Parent, Vector3 Position);

    public static void Run()
    {
        var joints = Body();
        var named = Infer(joints);
        Check(HumanoidSkeletonMapper.Complete(named) && named["leftHand"] == "LeftHand", "named anatomical chains");
        var anonymous = joints.Select(j => j with
        {
            Name = Anonymous(j.Name), Parent = Anonymous(j.Parent),
        }).ToList();
        var geometry = Infer(anonymous);
        Check(HumanoidSkeletonMapper.Complete(geometry) && geometry["leftUpperArm"] == "joint4" &&
            geometry["leftLowerArm"] == "joint5" && geometry["rightFoot"] == "joint12",
            "unnamed limbs use hierarchy, side, direction and segment lengths");
        Check(Equal(geometry, Infer(anonymous.AsEnumerable().Reverse())), "file order cannot determine assignment");
        var rotated = Meta(anonymous);
        rotated["ModelImporter"]["humanDescription"]["skeleton"].Seq[0].Map["rotation"] = Quat(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f));
        rotated["ModelImporter"]["humanDescription"]["skeleton"].Seq[0].Map["scale"] = Vec(new Vector3(3));
        Check(Equal(geometry, HumanoidSkeletonMapper.Infer(rotated)), "root rotation and scale preserve anatomical sides");
        rotated["ModelImporter"]["humanDescription"]["skeleton"].Seq[0].Map["scale"] = Vec(new Vector3(-3, 3, 3));
        Check(Equal(geometry, HumanoidSkeletonMapper.Infer(rotated)), "mirrored root retains left and right identities");
        var missingPose = Meta(anonymous);
        missingPose["ModelImporter"]["humanDescription"]["skeleton"].Seq[0].Map.Remove("position");
        Check(HumanoidSkeletonMapper.Infer(missingPose).Count == 0, "missing ancestor pose cannot supply geometry evidence");
        var twist = anonymous.ToList();
        int lower = twist.FindIndex(j => j.Name == "joint5");
        twist[lower] = twist[lower] with { Parent = "LeftArm_twist", Position = new Vector3(-.14f, 0, 0) };
        twist.Add(new("LeftArm_twist", "joint4", new Vector3(-.14f, 0, 0)));
        Check(Equal(geometry, Infer(twist)), "twist helper can be traversed without becoming an elbow");
        var namespaces = joints.Select(j => j with { Name = Prefix(j.Name), Parent = Prefix(j.Parent) });
        Check(Infer(namespaces).GetValueOrDefault("rightUpperLeg") == "mixamorig:RightUpLeg", "namespace and common limb aliases");

        var fingers = joints.ToList();
        fingers.Add(new("LeftHandIndex1", "LeftHand", new Vector3(-.04f, 0, 0)));
        fingers.Add(new("LeftHandIndex2", "LeftHandIndex1", new Vector3(-.03f, 0, 0)));
        fingers.Add(new("LeftHandIndex3", "LeftHandIndex2", new Vector3(-.02f, 0, 0)));
        Check(Infer(fingers).GetValueOrDefault("leftIndexDistal") == "LeftHandIndex3", "numbered finger chain");
        fingers[fingers.Count - 1] = fingers[^1] with { Parent = "RightHand" };
        Check(!Infer(fingers).ContainsKey("leftIndexProximal"), "cross-hand finger chain is not partially assigned");
        var face = joints.ToList();
        face.Add(new("J_Jaw", "Head", new Vector3(0, -.02f, .04f)));
        face.Add(new("J_F_D_Hair_2", "Head", new Vector3(0, -.03f, .04f)));
        Check(Infer(face).GetValueOrDefault("jaw") == "J_Jaw", "facial bones require name evidence; hair cannot become jaw");

        var duplicate = joints.Append(new Joint("LeftHand", "RightLowerArm", new Vector3(.25f, 0, 0)));
        Check(Infer(duplicate).Count == 0, "duplicate bone names do not select the first entry");
        var ambiguous = anonymous.Append(new Joint("anotherHand", "joint5", new Vector3(-.25f, 0, 0)));
        Check(Infer(ambiguous).Count == 0, "equally plausible limb endpoints remain unresolved");
        Check(Infer(joints.Where(j => j.Name != "RightHand")).Count == 0, "incomplete required skeleton is rejected");
        Check(Infer(joints.Select(j => j.Name == "Hips" ? j with { Parent = "Head" } : j)).Count == 0, "cycle is rejected");
        Check(Infer(joints.Select(j => j.Name == "LeftHand" ? j with { Parent = "missing" } : j)).Count == 0, "missing parent is rejected");
        Check(Infer(joints.Select(j => j.Name == "LeftHand" ? j with { Name = "L_Hand_R" } : j)).Count == 0, "contradictory side labels are rejected");
        Check(Infer(joints.Select(j => j.Name == "Hips" ? j with { Position = new Vector3(float.NaN) } : j)).Count == 0, "non-finite pose is rejected");
        var legacy = Meta(joints);
        foreach (var entry in legacy["ModelImporter"]["humanDescription"]["skeleton"].Seq) entry.Map.Remove("parentName");
        Check(HumanoidSkeletonMapper.Complete(HumanoidSkeletonMapper.Infer(legacy)), "old metadata without parent names retains unambiguous named bones");
        Console.WriteLine("Humanoid hierarchy and geometry mapping checks passed.");
    }

    public static void CheckParser(Func<string, string, string, string> asset)
    {
        const string guid = "fda00000000000000000000000000001";
        string path = asset("Assets/HumanoidFallback.fbx", guid, "; FBX 7.4.0 project file\n");
        string originalMeta = File.ReadAllText(path + ".meta");
        string skeleton = string.Join("", Body().Select(j => j with { Name = Anonymous(j.Name), Parent = Anonymous(j.Parent) }).Select(j =>
            FormattableString.Invariant($"    - name: {j.Name}\n      parentName: {j.Parent}\n      position: {{x: {j.Position.X}, y: {j.Position.Y}, z: {j.Position.Z}}}\n")));
        File.WriteAllText(path + ".meta", originalMeta + "\nModelImporter:\n  humanDescription:\n    human: []\n    skeleton:\n" + skeleton);
        using var package = UnityPackage.Open(path.Replace("HumanoidFallback.fbx", "Selected.prefab"));
        var classify = typeof(VrchatAvatarParser).GetMethod("GetHumanoidFbxKind", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Check(classify.Invoke(null, new object[] { package, guid }).ToString() == "Inferred", "primary model selection uses the same geometric fallback");
        File.WriteAllText(path + ".meta", originalMeta + "\nModelImporter:\n  humanDescription:\n    human:\n    - humanName: Hips\n      boneName: AuthoredCustomPelvis\n    skeleton:\n" + skeleton);
        var avatar = new VrchatAvatar { FbxGuid = guid };
        typeof(VrchatAvatarParser).GetMethod("ParseHumanoid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { package, avatar });
        Check(avatar.HumanBones.Count == 1 && avatar.HumanBones["hips"] == "AuthoredCustomPelvis", "explicit mapping is neither overwritten nor filled by inference");
    }

    private static List<Joint> Body()
    {
        var list = new List<Joint>
        {
            new("Root", "", Vector3.Zero), new("Hips", "Root", new(0, 1, 0)),
            new("Spine", "Hips", new(0, .2f, 0)), new("Chest", "Spine", new(0, .2f, 0)),
            new("Neck", "Chest", new(0, .2f, 0)), new("Head", "Neck", new(0, .15f, 0)),
        };
        foreach (var (side, x) in new[] { ("Left", -1f), ("Right", 1f) })
        {
            list.Add(new(side + "Shoulder", "Chest", new(x * .12f, .1f, 0)));
            list.Add(new(side + "UpperArm", side + "Shoulder", new(x * .12f, 0, 0)));
            list.Add(new(side + "LowerArm", side + "UpperArm", new(x * .28f, 0, 0)));
            list.Add(new(side + "Hand", side + "LowerArm", new(x * .25f, 0, 0)));
            list.Add(new(side + "UpperLeg", "Hips", new(x * .1f, -.05f, 0)));
            list.Add(new(side + "LowerLeg", side + "UpperLeg", new(0, -.45f, 0)));
            list.Add(new(side + "Foot", side + "LowerLeg", new(0, -.4f, .05f)));
        }
        return list;
    }
    private static string Anonymous(string name) => name switch
    {
        "LeftUpperArm" => "joint4", "LeftLowerArm" => "joint5", "LeftHand" => "joint6",
        "RightUpperArm" => "joint7", "RightLowerArm" => "joint8", "RightHand" => "joint9",
        "LeftUpperLeg" => "joint1", "LeftLowerLeg" => "joint2", "LeftFoot" => "joint3",
        "RightUpperLeg" => "joint10", "RightLowerLeg" => "joint11", "RightFoot" => "joint12", _ => name,
    };
    private static string Prefix(string name) => name == "" ? "" : "mixamorig:" + name.Replace("UpperLeg", "UpLeg").Replace("LowerArm", "ForeArm");
    private static Dictionary<string, string> Infer(IEnumerable<Joint> joints) => HumanoidSkeletonMapper.Infer(Meta(joints));
    private static YamlNode Meta(IEnumerable<Joint> joints) => Node(("ModelImporter", Node(("humanDescription", Node(("skeleton", new YamlNode
    {
        Seq = joints.Select(j => Node(("name", Scalar(j.Name)), ("parentName", Scalar(j.Parent)), ("position", Vec(j.Position)))).ToList(),
    }))))));
    private static YamlNode Node(params (string Key, YamlNode Value)[] entries) => new() { Map = entries.ToDictionary(e => e.Key, e => e.Value) };
    private static YamlNode Scalar(string text) => new() { ScalarValue = text };
    private static YamlNode Vec(Vector3 value) => Node(("x", Scalar(value.X.ToString(System.Globalization.CultureInfo.InvariantCulture))), ("y", Scalar(value.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))), ("z", Scalar(value.Z.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    private static YamlNode Quat(Quaternion value) { var n = Vec(new(value.X, value.Y, value.Z)); n.Map["w"] = Scalar(value.W.ToString(System.Globalization.CultureInfo.InvariantCulture)); return n; }
    private static bool Equal(Dictionary<string, string> a, Dictionary<string, string> b) => a.Count == b.Count && a.All(pair => b.GetValueOrDefault(pair.Key) == pair.Value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Humanoid mapping: " + message); }
}
