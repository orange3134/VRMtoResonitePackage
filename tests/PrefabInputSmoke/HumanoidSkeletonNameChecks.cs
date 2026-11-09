using VrmToResonitePackage.Vrchat;

internal static class HumanoidSkeletonNameChecks
{
    public static void Run()
    {
        // Aizara has human: [] and uses L_/R_ limbs and fingers, but LeftEye/RightEye.
        var bones = new (string Source, string Target)[]
        {
            ("Eye", "Eye"), ("Shoulder", "Shoulder"),
            ("UpperArm", "UpperArm"), ("LowerArm", "LowerArm"), ("Hand", "Hand"),
            ("UpperLeg", "UpperLeg"), ("LowerLeg", "LowerLeg"), ("Foot", "Foot"), ("Toes", "Toes"),
            ("Thumb_Proximal", "ThumbMetacarpal"), ("Thumb_Intermediate", "ThumbProximal"),
            ("Thumb_Distal", "ThumbDistal"),
            ("Index_Proximal", "IndexProximal"), ("Index_Intermediate", "IndexIntermediate"), ("Index_Distal", "IndexDistal"),
            ("Middle_Proximal", "MiddleProximal"), ("Middle_Intermediate", "MiddleIntermediate"), ("Middle_Distal", "MiddleDistal"),
            ("Ring_Proximal", "RingProximal"), ("Ring_Intermediate", "RingIntermediate"), ("Ring_Distal", "RingDistal"),
            ("Little_Proximal", "LittleProximal"), ("Little_Intermediate", "LittleIntermediate"), ("Little_Distal", "LittleDistal"),
        };
        foreach (var (source, target) in bones)
        foreach (var (letter, side) in new[] { ("L", "left"), ("R", "right") })
        {
            Expect(letter + "_" + source, side + target);
            Expect(side + source, side + target);
            Expect(source + "." + letter, side + target);
            Expect(source + "_" + letter, side + target);
        }
        Expect(" l_upperarm ", "leftUpperArm");
        Expect("RIGHTEye", "rightEye");
        Expect("Hips", "hips");
        Expect("UpperChest", "upperChest");
        foreach (string helper in new[] { "L_Bust_1", "R_hairB01", "neck_ex", "LeftHandTarget", "L_UpperArm_twist", "L_Hand_R", "Ring", "", null })
            Expect(helper, null);
        Console.WriteLine("Humanoid skeleton names: prefixed/suffixed limbs, fingers, eyes and helper exclusions passed.");
    }

    private static void Expect(string name, string expected)
    {
        string actual = VrchatConstants.InferHumanNameFromSkeletonName(name);
        if (actual != expected)
            throw new Exception($"Humanoid skeleton name '{name}': expected '{expected}', got '{actual}'.");
    }
}
