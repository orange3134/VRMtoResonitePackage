using Elements.Core;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Extracts simple Animator-driven face bindings without executing an Animator.</summary>
public static class VrchatAnimatorFaceParser
{
    private sealed record Shape(string Renderer, string Name, float Peak);

    public static void Apply(UnityPackage package, YamlNode descriptor, VrchatAvatar avatar)
    {
        var clips = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        var visemes = new Dictionary<int, HashSet<Shape>>();
        var unsupportedVisemes = new HashSet<int>();
        var blinks = new HashSet<Shape>();
        foreach (YamlNode layer in descriptor?["baseAnimationLayers"]?.Seq ?? new())
        {
            // SDK FX layer. Gesture/Action animations must not become always-on face drivers.
            if (layer["type"]?.AsInt() != 5 || layer["isDefault"]?.AsBool() == true) continue;
            UnityAsset asset = package.ByGuid(layer["animatorController"]?.Guid);
            if (asset?.Extension != ".controller" || !asset.HasContent) continue;
            UnityScene controller = package.ReadScene(asset);
            YamlNode settings = controller.Documents.Values.FirstOrDefault(d => d.ClassId == 91)?.Root;
            var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (YamlNode parameter in settings?["m_AnimatorParameters"]?.Seq ?? new())
            {
                string name = parameter["m_Name"]?.AsString();
                if (name == null) continue;
                defaults[name] = parameter["m_Type"]?.AsInt() switch
                {
                    4 => parameter["m_DefaultBool"]?.AsBool() == true ? 1 : 0,
                    3 => parameter["m_DefaultInt"]?.AsInt() ?? 0,
                    _ => parameter["m_DefaultFloat"]?.AsFloat() ?? 0,
                };
            }
            var initialStates = VrchatAnimatorDefaults.Resolve(controller, settings, defaults);
            int layerIndex = 0;
            foreach (YamlNode animatorLayer in settings?["m_AnimatorLayers"]?.Seq ?? new())
            {
                // Inferred drivers emit full-weight expressions; partial layer contributions
                // cannot be represented without changing the deformation.
                if (layerIndex++ > 0 && (animatorLayer["m_DefaultWeight"]?.AsFloat() ?? 0) != 1f) continue;
                var reachable = new HashSet<long>();
                Gather(animatorLayer["m_StateMachine"]?.FileID ?? 0);
                // Permanent drivers cannot preserve a toggle/parameter gate, even when
                // its startup default is enabled. Reject such layers conservatively.
                bool gated = reachable.Any(id => (controller.Doc(id)?.Root?["m_Conditions"]?.Seq ?? new())
                    .Any(condition => condition["m_ConditionEvent"]?.AsString() != "Viseme"));
                reachable.Clear();
                Gather(animatorLayer["m_StateMachine"]?.FileID ?? 0, followDestinations: true);
                if (descriptor["lipSync"]?.AsInt() == 4 && !gated)
                {
                    foreach (long id in reachable)
                    {
                        YamlNode transition = controller.Doc(id)?.Root;
                        var conditions = transition?["m_Conditions"]?.Seq;
                        // Only an unambiguous Viseme == N binding is directly representable.
                        if (conditions?.Count != 1 || transition["m_Mute"]?.AsBool() == true) continue;
                        YamlNode condition = conditions[0];
                        if (condition["m_ConditionEvent"]?.AsString() != "Viseme" ||
                            condition["m_ConditionMode"]?.AsInt() != 6) continue;
                        float threshold = condition["m_EventTreshold"]?.AsFloat(-1) ?? -1;
                        if (threshold < 0 || threshold > 14 || threshold != MathF.Truncate(threshold)) continue;
                        YamlNode state = controller.Doc(transition["m_DstState"]?.FileID ?? 0)?.Root;
                        var shapes = StableShapes(state, (int)threshold);
                        RecordViseme((int)threshold, shapes);
                    }
                    // Some controllers use an unconditional entry fallback for silence,
                    // after explicit entries for all fourteen spoken phonemes.
                    YamlNode machine = controller.Doc(animatorLayer["m_StateMachine"]?.FileID ?? 0)?.Root;
                    var entries = VrchatAnimatorDefaults.ActiveTransitions(controller, machine?["m_EntryTransitions"]?.Seq)
                        .Where(e => reachable.Contains(e.FileID ?? 0))
                        .Select(e => controller.Doc(e.FileID ?? 0)?.Root).ToList();
                    var spoken = entries.Take(Math.Max(0, entries.Count - 1)).Select(e => e?["m_Conditions"]?.Seq)
                        .Where(c => c?.Count == 1 && c[0]["m_ConditionEvent"]?.AsString() == "Viseme" &&
                            c[0]["m_ConditionMode"]?.AsInt() == 6)
                        .Select(c => c[0]["m_EventTreshold"]?.AsFloat(-1) ?? -1).ToHashSet();
                    YamlNode fallback = entries.LastOrDefault();
                    if (Enumerable.Range(1, 14).All(i => spoken.Contains(i)) &&
                        fallback?["m_Conditions"]?.Seq?.Count == 0 && fallback["m_Mute"]?.AsBool() != true)
                    {
                        var shapes = StableShapes(controller.Doc(fallback["m_DstState"]?.FileID ?? 0)?.Root, 0);
                        RecordViseme(0, shapes);
                    }
                }
                if (avatar.Blink == null)
                {
                    initialStates.TryGetValue(animatorLayer["m_StateMachine"]?.FileID ?? 0, out YamlNode state);
                    YamlNode clip = Clip(state?["m_Motion"]);
                    var shapes = ActiveShapes(clip);
                    if (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() == true &&
                        shapes.Count == 1 && MathF.Abs(shapes[0].Peak - 100) < 0.01f &&
                        string.Equals(shapes[0].Name, "blink", StringComparison.OrdinalIgnoreCase))
                        blinks.Add(shapes[0]);
                }

                List<Shape> StableShapes(YamlNode state, int viseme)
                {
                    // A permanent driver cannot reproduce an expression that departs while
                    // this phoneme is still active, even if departure waits for exit time.
                    foreach (YamlNode reference in VrchatAnimatorDefaults.ActiveTransitions(controller, state?["m_Transitions"]?.Seq))
                    {
                        YamlNode departure = controller.Doc(reference.FileID ?? 0)?.Root;
                        if (departure == null || (departure["m_Conditions"]?.Seq ?? new()).All(condition =>
                            condition["m_ConditionEvent"]?.AsString() != "Viseme" || MatchesViseme(condition, viseme)))
                            return new();
                    }
                    return ActiveShapes(Clip(state?["m_Motion"]));
                }

                void Gather(long id, bool followDestinations = false)
                {
                    if (id == 0 || !reachable.Add(id)) return;
                    YamlNode node = controller.Doc(id)?.Root;
                    foreach (string key in new[] { "m_DstState", "m_DstStateMachine" })
                        Gather(node?[key]?.FileID ?? 0, followDestinations);
                    if (!followDestinations)
                    {
                        foreach (YamlNode state in node?["m_ChildStates"]?.Seq ?? new()) Gather(state["m_State"]?.FileID ?? 0);
                        foreach (YamlNode machine in node?["m_ChildStateMachines"]?.Seq ?? new()) Gather(machine["m_StateMachine"]?.FileID ?? 0);
                    }
                    foreach (string key in new[] { "m_Transitions", "m_EntryTransitions", "m_AnyStateTransitions" })
                    {
                        var remaining = Enumerable.Range(0, 15).ToHashSet();
                        foreach (YamlNode transition in VrchatAnimatorDefaults.ActiveTransitions(controller, node?[key]?.Seq))
                        {
                            YamlNode candidate = controller.Doc(transition.FileID ?? 0)?.Root;
                            var conditions = candidate?["m_Conditions"]?.Seq ?? new();
                            if (followDestinations && conditions.All(c => c["m_ConditionEvent"]?.AsString() == "Viseme"))
                            {
                                var eligible = remaining.Where(value => conditions.All(c => MatchesViseme(c, value))).ToArray();
                                if (eligible.Length == 0) continue;
                                // Timed transitions do not always win before later siblings.
                                if (candidate?["m_HasExitTime"]?.AsBool() != true) remaining.ExceptWith(eligible);
                            }
                            Gather(transition.FileID ?? 0, followDestinations);
                        }
                        // Default is the fallback only for values not routed by Entry.
                        if (key == "m_EntryTransitions" && (!followDestinations || remaining.Count > 0))
                            Gather(node?["m_DefaultState"]?.FileID ?? 0, followDestinations);
                    }
                }


            }
        }
        foreach ((string preset, int index) in VrchatConstants.VisemeToVrcSlot())
        {
            if (unsupportedVisemes.Contains(index) || !visemes.TryGetValue(index, out var candidates) || candidates.Count != 1) continue;
            Shape shape = candidates.Single();
            avatar.Visemes.Add(new VrchatViseme { ResonitePreset = preset, MeshGameObjectPath = shape.Renderer,
                MeshGameObjectName = shape.Renderer.Split('/').Last(), BlendShapeName = shape.Name });
        }
        if (avatar.Blink == null && blinks.Count == 1)
        {
            Shape shape = blinks.Single();
            avatar.Blink = new VrchatBlink { MeshGameObjectPath = shape.Renderer,
                MeshGameObjectName = shape.Renderer.Split('/').Last(), BlendShapeName = shape.Name, BlendShapeIndex = -1 };
        }
        UniLog.Log($"Animator face bindings: {avatar.Visemes.Count} viseme(s), blink={avatar.Blink?.BlendShapeName ?? "(descriptor/none)"}");

        void RecordViseme(int index, List<Shape> shapes)
        {
            // Do not let an unsupported competing motion disappear from the ambiguity check.
            // A DirectVisemeDriver can represent only one full-weight shape per phoneme.
            if (shapes.Count != 1 || MathF.Abs(shapes[0].Peak - 100) >= 0.01f)
            {
                unsupportedVisemes.Add(index);
                return;
            }
            if (!visemes.TryGetValue(index, out var candidates)) visemes[index] = candidates = new();
            candidates.Add(shapes[0]);
        }

        YamlNode Clip(YamlNode motion)
        {
            string guid = motion?.Guid;
            if (guid == null) return null;
            if (clips.TryGetValue(guid, out var cached)) return cached;
            UnityAsset asset = package.ByGuid(guid);
            YamlNode clip = asset?.Extension == ".anim" && asset.HasContent
                ? package.ReadScene(asset).Doc(motion.FileID ?? 7400000)?.Root : null;
            clips[guid] = clip;
            return clip;
        }
    }

    private static bool MatchesViseme(YamlNode condition, int value)
    {
        float threshold = condition["m_EventTreshold"]?.AsFloat() ?? 0;
        return condition["m_ConditionMode"]?.AsInt() switch
        {
            1 => value != 0, 2 => value == 0, 3 => value > threshold, 4 => value < threshold,
            6 => value == threshold, 7 => value != threshold, _ => false,
        };
    }

    private static List<Shape> ActiveShapes(YamlNode clip)
    {
        var result = new List<Shape>();
        foreach (YamlNode curve in clip?["m_FloatCurves"]?.Seq ?? new())
        {
            string attribute = curve["attribute"]?.AsString();
            string path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true ||
                path == null) continue;
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            float peak = keys?.Select(key => key["value"]?.AsFloat() ?? 0).DefaultIfEmpty().Max() ?? 0;
            if (peak > 0.001f)
                result.Add(new Shape(path, attribute["blendShape.".Length..], peak));
        }
        return result;
    }
}
