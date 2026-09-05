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
                if (layerIndex++ > 0 && (animatorLayer["m_DefaultWeight"]?.AsFloat() ?? 0) <= 0) continue;
                var reachable = new HashSet<long>();
                Gather(animatorLayer["m_StateMachine"]?.FileID ?? 0);
                if (descriptor["lipSync"]?.AsInt() == 4)
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
                        var shapes = ActiveShapes(Clip(state?["m_Motion"]));
                        if (!visemes.TryGetValue((int)threshold, out var candidates))
                            visemes[(int)threshold] = candidates = new HashSet<Shape>();
                        if (shapes.Count == 1 && MathF.Abs(shapes[0].Peak - 100) < 0.01f)
                            candidates.Add(shapes[0]);
                    }
                    // Some controllers use an unconditional entry fallback for silence,
                    // after explicit entries for all fourteen spoken phonemes.
                    YamlNode machine = controller.Doc(animatorLayer["m_StateMachine"]?.FileID ?? 0)?.Root;
                    var entries = (machine?["m_EntryTransitions"]?.Seq ?? new())
                        .Select(e => controller.Doc(e.FileID ?? 0)?.Root).ToList();
                    var spoken = entries.Take(Math.Max(0, entries.Count - 1)).Select(e => e?["m_Conditions"]?.Seq)
                        .Where(c => c?.Count == 1 && c[0]["m_ConditionEvent"]?.AsString() == "Viseme" &&
                            c[0]["m_ConditionMode"]?.AsInt() == 6)
                        .Select(c => c[0]["m_EventTreshold"]?.AsFloat(-1) ?? -1).ToHashSet();
                    YamlNode fallback = entries.LastOrDefault();
                    if (Enumerable.Range(1, 14).All(i => spoken.Contains(i)) &&
                        fallback?["m_Conditions"]?.Seq?.Count == 0 && fallback["m_Mute"]?.AsBool() != true)
                    {
                        var shapes = ActiveShapes(Clip(controller.Doc(fallback["m_DstState"]?.FileID ?? 0)?.Root?["m_Motion"]));
                        if (shapes.Count == 1 && MathF.Abs(shapes[0].Peak - 100) < 0.01f)
                        {
                            if (!visemes.TryGetValue(0, out var candidates)) visemes[0] = candidates = new();
                            candidates.Add(shapes[0]);
                        }
                    }
                }
                if (avatar.Blink == null)
                {
                    initialStates.TryGetValue(animatorLayer["m_StateMachine"]?.FileID ?? 0, out YamlNode state);
                    YamlNode clip = Clip(state?["m_Motion"]);
                    var shapes = ActiveShapes(clip);
                    if (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() == true &&
                        shapes.Count == 1 && string.Equals(shapes[0].Name, "blink", StringComparison.OrdinalIgnoreCase))
                        blinks.Add(shapes[0]);
                }

                void Gather(long id)
                {
                    if (id == 0 || !reachable.Add(id)) return;
                    YamlNode node = controller.Doc(id)?.Root;
                    foreach (YamlNode state in node?["m_ChildStates"]?.Seq ?? new()) Gather(state["m_State"]?.FileID ?? 0);
                    foreach (YamlNode machine in node?["m_ChildStateMachines"]?.Seq ?? new()) Gather(machine["m_StateMachine"]?.FileID ?? 0);
                    foreach (string key in new[] { "m_Transitions", "m_EntryTransitions", "m_AnyStateTransitions" })
                        foreach (YamlNode transition in node?[key]?.Seq ?? new()) Gather(transition.FileID ?? 0);
                }


            }
        }
        foreach ((string preset, int index) in VrchatConstants.VisemeToVrcSlot())
        {
            if (!visemes.TryGetValue(index, out var candidates) || candidates.Count != 1) continue;
            Shape shape = candidates.Single();
            avatar.Visemes.Add(new VrchatViseme { ResonitePreset = preset, MeshGameObjectName = shape.Renderer, BlendShapeName = shape.Name });
        }
        if (avatar.Blink == null && blinks.Count == 1)
        {
            Shape shape = blinks.Single();
            avatar.Blink = new VrchatBlink { MeshGameObjectName = shape.Renderer, BlendShapeName = shape.Name, BlendShapeIndex = -1 };
        }
        UniLog.Log($"Animator face bindings: {avatar.Visemes.Count} viseme(s), blink={avatar.Blink?.BlendShapeName ?? "(descriptor/none)"}");

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

    private static List<Shape> ActiveShapes(YamlNode clip)
    {
        var result = new List<Shape>();
        foreach (YamlNode curve in clip?["m_FloatCurves"]?.Seq ?? new())
        {
            string attribute = curve["attribute"]?.AsString();
            string path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true ||
                string.IsNullOrEmpty(path)) continue;
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            float peak = keys?.Select(key => key["value"]?.AsFloat() ?? 0).DefaultIfEmpty().Max() ?? 0;
            if (peak > 0.001f)
                result.Add(new Shape(path.Split('/').Last(), attribute["blendShape.".Length..], peak));
        }
        return result;
    }
}
