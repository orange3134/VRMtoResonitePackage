using System.Numerics;
using System.Text.RegularExpressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>
/// Independent, conservative fallback for an absent ModelImporter human map.
/// Uses anatomical chains and rest-pose geometry, with names as anchors and evidence.
/// This does not execute Unity or reproduce its automapper's scoring/tie breaking.
/// </summary>
internal static class HumanoidSkeletonMapper
{
    internal static readonly string[] RequiredBones =
    {
        "hips", "spine", "head",
        "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm", "rightLowerArm", "rightHand",
        "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot",
    };

    private sealed class Bone
    {
        public string Name, ParentName, Hint;
        public Bone Parent;
        public Matrix4x4 Local, World;
        public int State;
        public bool HasPosition, PoseComplete;
        public Vector3 Position => World.Translation;
    }

    public static Dictionary<string, string> Infer(YamlNode meta)
    {
        var entries = meta?["ModelImporter"]?["humanDescription"]?["skeleton"]?.Seq;
        var empty = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entries == null || entries.Count > 4096) return empty;
        var bones = entries.Select(entry => new Bone
        {
            Name = entry?["name"]?.AsString(), ParentName = entry?["parentName"]?.AsString(),
            Hint = NameHint(entry?["name"]?.AsString()), HasPosition = entry?["position"] != null,
            Local = Transform(entry),
        }).Where(b => !string.IsNullOrWhiteSpace(b.Name)).ToArray();
        var groups = bones.GroupBy(b => b.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray());
        // parentName alone cannot disambiguate duplicate names. Never choose by file order.
        foreach (var b in bones)
        {
            if (groups[b.Name].Length != 1) b.State = -1;
            if (!string.IsNullOrEmpty(b.ParentName))
            {
                if (!groups.TryGetValue(b.ParentName, out var parents) || parents.Length != 1) b.State = -1;
                else b.Parent = parents[0];
            }
        }
        foreach (var b in bones) ResolveWorld(b, 0);
        var valid = bones.Where(b => b.State == 2).ToArray();
        if (!valid.Any(b => b.Parent != null))
        {
            // Older metadata can lack parentName. Preserve only unique exact-name evidence.
            var named = valid.Where(b => !IsHelper(b) && (b.Hint is "hips" or "spine" or "chest" or "upperChest" or "neck" or "head" or "jaw" ||
                    b.Hint?.StartsWith("left", StringComparison.Ordinal) == true || b.Hint?.StartsWith("right", StringComparison.Ordinal) == true))
                .GroupBy(b => b.Hint).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Name);
            return Complete(named) ? named : empty;
        }

        Bone hips = Unique(valid, "hips"), head = Unique(valid, "head");
        // Without reliable central anchors geometry alone can mistake clothing for a body.
        if (hips == null || head == null) return empty;
        var torso = Path(hips, head);
        if (torso == null || torso.Count < 2) return empty;
        torso = torso.Where(b => !IsHelper(b)).ToList();
        var map = new Dictionary<string, Bone>(StringComparer.Ordinal) { ["hips"] = hips, ["head"] = head };
        var interior = torso.Where(b => b != head).ToArray();
        foreach (string role in new[] { "spine", "chest", "upperChest", "neck" })
            if (Unique(interior, role) is {} bone) map[role] = bone;
        if (!map.ContainsKey("spine") && interior.FirstOrDefault(b => b.Hint == null) is {} spine) map["spine"] = spine;
        if (!map.ContainsKey("spine")) return empty;

        Vector3 up = head.Position - hips.Position;
        float height = up.Length();
        bool geometry = head.PoseComplete && height > 1e-6f;
        up = geometry ? up / height : Vector3.UnitY;
        Bone root = hips;
        while (root.Parent != null) root = root.Parent;
        Vector3 right = Vector3.TransformNormal(Vector3.UnitX, root.World);
        foreach (string role in new[] { "UpperLeg", "UpperArm", "Hand" })
        {
            Bone leftBone = Unique(valid, "left" + role), rightBone = Unique(valid, "right" + role);
            if (leftBone != null && rightBone != null &&
                Path(hips, leftBone) != null && Path(hips, rightBone) != null &&
                Vector3.DistanceSquared(leftBone.Position, rightBone.Position) > 1e-12f)
            {
                right = rightBone.Position - leftBone.Position;
                break;
            }
        }
        right -= up * Vector3.Dot(right, up);
        if (right.LengthSquared() < 1e-12f) geometry = false;
        else right = Vector3.Normalize(right);

        foreach (string side in new[] { "left", "right" })
        foreach (bool arm in new[] { false, true })
        {
            string[] roles = arm ? new[] { "UpperArm", "LowerArm", "Hand" } : new[] { "UpperLeg", "LowerLeg", "Foot" };
            var choices = new List<(Bone[] Bones, float Score)>();
            foreach (var end in valid.Where(b => CanBe(b, side, roles[2])))
            {
                var path = Path(hips, end);
                if (path == null || path.Any(IsHelperBranch)) continue;
                Bone anchor = arm ? path.LastOrDefault(torso.Contains) : hips;
                if (anchor == null || anchor == head || (!arm && path.Any(torso.Contains))) continue;
                var limb = path.SkipWhile(b => b != anchor).Skip(1).ToArray();
                if (!arm) limb = path.ToArray();
                limb = limb.Where(b => !IsHelper(b)).ToArray();
                if (limb.Length < 3 || limb.Length > 7) continue;
                for (int i = 0; i < limb.Length - 2; i++)
                for (int j = i + 1; j < limb.Length - 1; j++)
                {
                    var chain = new[] { limb[i], limb[j], end };
                    if (!CanBe(chain[0], side, roles[0]) || !CanBe(chain[1], side, roles[1])) continue;
                    int evidence = Enumerable.Range(0, 3).Sum(k => NameScore(chain[k], side + roles[k]));
                    bool allNamed = Enumerable.Range(0, 3).All(k => chain[k].Hint == side + roles[k]);
                    float shapeScore = 0;
                    if (!allNamed)
                    {
                        if (!geometry || !chain.All(b => b.PoseComplete)) continue;
                        Vector3 a = chain[1].Position - chain[0].Position, b = chain[2].Position - chain[1].Position;
                        float lenA = a.Length(), lenB = b.Length();
                        if (lenA < height * .08f || lenB < height * .08f ||
                            (lenA + lenB) < height * .4f || (lenA + lenB) > height * 3f) continue;
                        float ratio = MathF.Min(lenA, lenB) / MathF.Max(lenA, lenB);
                        if (ratio < .35f) continue;
                        float sign = side == "left" ? -1 : 1;
                        if (Vector3.Dot(end.Position - hips.Position, right) * sign < height * .06f) continue;
                        Vector3 direction = arm ? right * sign : -up;
                        if (Vector3.Dot(Vector3.Normalize(a), direction) < .35f ||
                            Vector3.Dot(Vector3.Normalize(b), direction) < .35f) continue;
                        if (!arm && Vector3.Dot(chain[0].Position - hips.Position, up) > height * .15f) continue;
                        shapeScore = ratio * 40f - i - (j - i - 1) * 3f;
                    }
                    choices.Add((chain, evidence + shapeScore));
                }
            }
            var ordered = choices.OrderByDescending(c => c.Score).ToArray();
            if (ordered.Length == 0 || (ordered.Length > 1 && ordered[0].Score - ordered[1].Score < 2f)) return empty;
            for (int k = 0; k < 3; k++) map[side + roles[k]] = ordered[0].Bones[k];
        }

        // Fill optional torso joints only along the selected central chain.
        var availableTorso = interior.Where(b => !map.ContainsValue(b) && b.Hint == null).ToList();
        if (!map.ContainsKey("neck") && availableTorso.Count >= 2)
        {
            map["neck"] = availableTorso[^1];
            availableTorso.RemoveAt(availableTorso.Count - 1);
        }
        foreach (string role in new[] { "chest", "upperChest" })
            if (!map.ContainsKey(role) && availableTorso.Count > 0)
            {
                map[role] = availableTorso[0];
                availableTorso.RemoveAt(0);
            }
        foreach (string side in new[] { "left", "right" })
        {
            AddOptional(side + "Shoulder", hips, map[side + "UpperArm"]);
            AddOptional(side + "Toes", map[side + "Foot"]);
            AddOptional(side + "Eye", head);
            foreach (string finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                string[] joints = finger == "Thumb" ? new[] { "Metacarpal", "Proximal", "Distal" } : new[] { "Proximal", "Intermediate", "Distal" };
                var chain = joints.Select(j => Unique(valid, side + finger + j)).ToArray();
                Bone parent = map[side + "Hand"];
                bool ok = true;
                foreach (var bone in chain)
                {
                    var path = bone == null ? null : Path(parent, bone);
                    if (path == null || path.Count > 4 || path.Any(IsHelperBranch)) { ok = false; break; }
                    parent = bone;
                }
                if (ok) for (int k = 0; k < 3; k++) map[side + finger + joints[k]] = chain[k];
            }
        }
        AddOptional("jaw", head);
        if (map.Values.Distinct().Count() != map.Count || !ValidTorso()) return empty;
        return map.ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.Ordinal);

        void AddOptional(string role, Bone parent, Bone child = null)
        {
            Bone bone = Unique(valid, role);
            var path = bone == null ? null : Path(parent, bone);
            if (path != null && !path.Any(IsHelperBranch) && (child == null || Path(bone, child) != null) && !map.ContainsValue(bone)) map[role] = bone;
        }
        bool ValidTorso()
        {
            Bone previous = hips;
            foreach (string role in new[] { "spine", "chest", "upperChest", "neck", "head" })
                if (map.TryGetValue(role, out var bone))
                {
                    if (Path(previous, bone) == null) return false;
                    previous = bone;
                }
            return true;
        }
    }

    internal static bool Complete(IReadOnlyDictionary<string, string> map) => RequiredBones.All(map.ContainsKey);
    private static Bone Unique(IEnumerable<Bone> bones, string role)
    {
        var matches = bones.Where(b => b.Hint == role && !IsHelper(b)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static List<Bone> Path(Bone ancestor, Bone node)
    {
        if (ancestor == node) return null;
        var path = new List<Bone>();
        for (var b = node; b != null; b = b.Parent)
        {
            if (b == ancestor) { path.Reverse(); return path; }
            path.Add(b);
        }
        return null;
    }
    private static bool IsHelperBranch(Bone b) => Regex.IsMatch(b.Name, "hair|skirt|cloth|bust|breast|tail|wing|weapon", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsHelper(Bone b) => IsHelperBranch(b) || Regex.IsMatch(b.Name, "twist|helper|(?:^|[_. ])(?:end|nub|tip)(?:$|[_. 0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool CanBe(Bone b, string side, string role) => !IsHelper(b) &&
        (b.Hint == null || b.Hint == side + role || b.Hint == char.ToLowerInvariant(role[0]) + role[1..]);
    private static int NameScore(Bone b, string role) => b.Hint == role ? 100 : b.Hint != null ? 40 : 0;

    private static string NameHint(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name[(name.LastIndexOf(':') + 1)..];
        name = Regex.Replace(name, @"^Bip\d*[ _]+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        name = Regex.Replace(name, @"^J_Bip_([LRC])_", "$1_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (name.StartsWith("C_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("J_", StringComparison.OrdinalIgnoreCase)) name = name[2..];
        if (VrchatConstants.InferHumanNameFromSkeletonName(name) is {} exact) return exact;
        var words = Regex.Replace(name, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
        var tokens = Regex.Split(words, "[^a-z0-9]+").Where(t => t.Length > 0).ToList();
        string side = null;
        if (tokens.Remove("left") || tokens.Remove("l")) side = "left";
        if (tokens.Remove("right") || tokens.Remove("r")) { if (side != null) return "invalid"; side = "right"; }
        string token = string.Concat(tokens);
        if (side == null && token is "spine1" or "spine01") return "chest";
        if (side == null && token is "spine2" or "spine02") return "upperChest";
        var numbered = Regex.Match(token, @"^(?:hand)?(thumb|index|middle|ring|little|pinky)([123])$");
        if (side != null && numbered.Success)
        {
            string finger = numbered.Groups[1].Value;
            if (finger == "pinky") finger = "little";
            int index = int.Parse(numbered.Groups[2].Value) - 1;
            string joint = (finger == "thumb" ? new[] { "Metacarpal", "Proximal", "Distal" } : new[] { "Proximal", "Intermediate", "Distal" })[index];
            return side + char.ToUpperInvariant(finger[0]) + finger[1..] + joint;
        }
        string part = token switch
        {
            "shoulder" or "collar" or "clavicle" => "Shoulder", "arm" or "upperarm" => "UpperArm",
            "forearm" or "lowerarm" or "elbow" => "LowerArm", "hand" or "wrist" => "Hand",
            "thigh" or "upperleg" or "upleg" => "UpperLeg", "shin" or "calf" or "leg" or "lowerleg" or "knee" => "LowerLeg",
            "foot" or "ankle" => "Foot", "toe" or "toes" or "toebase" or "toe0" => "Toes",
            _ => null,
        };
        return part == null ? null : side != null ? side + part : char.ToLowerInvariant(part[0]) + part[1..];
    }

    private static Matrix4x4 Transform(YamlNode entry)
    {
        Vector3 p = new(entry?["position"]?.Vec("x") ?? 0, entry?["position"]?.Vec("y") ?? 0, entry?["position"]?.Vec("z") ?? 0);
        var r = entry?["rotation"];
        Quaternion q = new(r?.Vec("x") ?? 0, r?.Vec("y") ?? 0, r?.Vec("z") ?? 0, r?.Vec("w", 1) ?? 1);
        if (!float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-12f) return new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var s = entry?["scale"];
        return Matrix4x4.CreateScale(s?.Vec("x", 1) ?? 1, s?.Vec("y", 1) ?? 1, s?.Vec("z", 1) ?? 1) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) * Matrix4x4.CreateTranslation(p);
    }
    private static bool ResolveWorld(Bone b, int depth)
    {
        if (b.State == 2) return true;
        if (b.State != 0 || depth > 256) { b.State = -1; return false; }
        b.State = 1;
        if (b.Parent != null && !ResolveWorld(b.Parent, depth + 1)) { b.State = -1; return false; }
        b.World = b.Parent == null ? b.Local : b.Local * b.Parent.World;
        b.PoseComplete = b.HasPosition && (b.Parent?.PoseComplete ?? true);
        if (!float.IsFinite(b.World.GetDeterminant()) || !float.IsFinite(b.Position.LengthSquared())) { b.State = -1; return false; }
        b.State = 2;
        return true;
    }
}
