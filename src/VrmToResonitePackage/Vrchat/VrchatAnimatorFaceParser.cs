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
        var blinkLoops = new List<VrchatBlinkLoop>();
        var neutralBlinks = new List<VrchatBlinkLoop>();
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
            var graph = new VrchatAnimatorGraph(controller);
            var owners = graph.Owners;
            var animatorLayers = settings?["m_AnimatorLayers"]?.Seq ?? new();
            var layerBindings = animatorLayers.Select((item, index) =>
                index == 0 || (item["m_DefaultWeight"]?.AsFloat() ?? 0) != 0
                    ? LayerBindings(item) : new HashSet<(string, string)>()).ToArray();
            int layerIndex = 0;
            foreach (YamlNode animatorLayer in settings?["m_AnimatorLayers"]?.Seq ?? new())
            {
                // Inferred drivers emit full-weight expressions; partial or additive contributions
                // cannot be represented without changing the deformation.
                if (layerIndex++ > 0 && ((animatorLayer["m_DefaultWeight"]?.AsFloat() ?? 0) != 1f ||
                    animatorLayer["m_BlendingMode"]?.AsInt() == 1)) continue;
                var reachable = graph.Reachable(animatorLayer["m_StateMachine"]?.FileID ?? 0);
                // Permanent drivers cannot preserve a toggle/parameter gate, even when
                // its startup default is enabled. Reject such layers conservatively.
                bool gated = reachable.Any(id => (controller.Doc(id)?.Root?["m_Conditions"]?.Seq ?? new())
                    .Any(condition => condition["m_ConditionEvent"]?.AsString() != "Viseme"));
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
                        YamlNode state = Destination(transition, (int)threshold, new());
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
                        var shapes = StableShapes(Destination(fallback, 0, new()), 0);
                        RecordViseme(0, shapes);
                    }
                }
                if (avatar.Blink == null)
                {
                    initialStates.TryGetValue(animatorLayer["m_StateMachine"]?.FileID ?? 0, out YamlNode state);
                    YamlNode clip = Clip(state?["m_Motion"]);
                    var loop = VrchatBlinkLoop.Read(clip);
                    bool neutral = loop == null;
                    loop ??= VrchatBlinkLoop.ReadNeutral(clip);
                    if (loop != null)
                    {
                        // Earlier face layers may share these curves: the active blink layer
                        // overrides them at startup, and exported expressions use the native mixer.
                        // A later active layer writing any required curve would override the loop.
                        bool overwritten = animatorLayers.Skip(layerIndex).Any(l =>
                        {
                            if ((l["m_DefaultWeight"]?.AsFloat() ?? 0) == 0) return false;
                            initialStates.TryGetValue(l["m_StateMachine"]?.FileID ?? 0, out var later);
                            // Unknown startup motion is not evidence of a non-conflicting layer.
                            if (later == null) return LayerBindings(l).Any(b =>
                                loop.InitialValues.Keys.Any(k => k.Path == b.Item1 && k.Shape == b.Item2));
                            var motion = later["m_Motion"];
                            if ((motion?.FileID ?? 0) == 0) return false;
                            var laterClip = Clip(motion);
                            if (laterClip == null)
                            {
                                var bindings = MotionBindings(motion, out bool complete);
                                return !complete || bindings.Any(b =>
                                    loop.InitialValues.Keys.Any(k => k.Path == b.Item1 && k.Shape == b.Item2));
                            }
                            return (laterClip["m_FloatCurves"]?.Seq ?? new()).Any(c =>
                                c["classID"]?.AsInt() == 137 && loop.InitialValues.Keys.Any(b =>
                                    b.Path == c["path"]?.AsString() && "blendShape." + b.Shape == c["attribute"]?.AsString()));
                        });
                        if (!overwritten) (neutral ? neutralBlinks : blinkLoops).Add(loop);
                        else UniLog.Log($"Animator blink loop skipped: {clip["m_Name"]?.AsString()} is overridden or has an unresolved later layer.");
                    }
                    var shapes = ActiveShapes(clip, requireOpen: true);
                    if (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() == true &&
                        shapes.Count == 1 && MathF.Abs(shapes[0].Peak - 100) < 0.01f &&
                        string.Equals(shapes[0].Name, "blink", StringComparison.OrdinalIgnoreCase) &&
                        !HasCompetingBindings(shapes))
                        blinks.Add(shapes[0]);
                }

                bool HasCompetingBindings(List<Shape> shapes) =>
                    shapes.Any(shape => layerBindings.Where((_, index) => index != layerIndex - 1)
                        .Any(bindings => bindings.Contains((shape.Renderer, shape.Name))));

                YamlNode Destination(YamlNode transition, int viseme, HashSet<long> visited)
                {
                    if (transition == null || transition["m_IsExit"]?.AsBool() == true) return null;
                    long state = transition["m_DstState"]?.FileID ?? 0;
                    if (state != 0) return controller.Doc(state)?.Root;
                    long machine = transition["m_DstStateMachine"]?.FileID ?? 0;
                    if (machine == 0 || !visited.Add(machine)) return null;
                    YamlNode node = controller.Doc(machine)?.Root;
                    // Entry routing preserves the incoming phoneme. Apply list priority and
                    // Solo/Mute before falling back to the nested machine's default state.
                    foreach (var reference in VrchatAnimatorDefaults.ActiveTransitions(controller, node?["m_EntryTransitions"]?.Seq))
                    {
                        YamlNode entry = controller.Doc(reference.FileID ?? 0)?.Root;
                        if (!(entry?["m_Conditions"]?.Seq ?? new()).All(condition =>
                            condition["m_ConditionEvent"]?.AsString() == "Viseme" && VrchatAnimatorGraph.Matches(condition, viseme))) continue;
                        return Destination(entry, viseme, visited);
                    }
                    return controller.Doc(node?["m_DefaultState"]?.FileID ?? 0)?.Root;
                }

                List<Shape> StableShapes(YamlNode state, int viseme)
                {
                    // A permanent driver cannot reproduce an expression that departs while
                    // this phoneme is still active, even if departure waits for exit time.
                    foreach (YamlNode reference in VrchatAnimatorDefaults.ActiveTransitions(controller, state?["m_Transitions"]?.Seq))
                    {
                        YamlNode departure = controller.Doc(reference.FileID ?? 0)?.Root;
                        if (departure == null || (departure["m_Conditions"]?.Seq ?? new()).All(condition =>
                            condition["m_ConditionEvent"]?.AsString() != "Viseme" || VrchatAnimatorGraph.Matches(condition, viseme)))
                            return new();
                    }
                    var visited = new HashSet<YamlNode>();
                    var current = state;
                    while (current != null && owners.TryGetValue(current, out var machine) && visited.Add(machine))
                    {
                        foreach (var reference in VrchatAnimatorDefaults.ActiveTransitions(controller, machine["m_AnyStateTransitions"]?.Seq))
                        {
                            var departure = controller.Doc(reference.FileID ?? 0)?.Root;
                            if (departure == null) return new();
                            if (!(departure["m_Conditions"]?.Seq ?? new()).All(condition =>
                                condition["m_ConditionEvent"]?.AsString() != "Viseme" || VrchatAnimatorGraph.Matches(condition, viseme))) continue;
                            bool self = controller.Doc(departure["m_DstState"]?.FileID ?? 0)?.Root == state;
                            if (!self) return new();
                            if (departure["m_CanTransitionToSelf"]?.AsBool() == false) continue;
                            // A self route retains this motion and can shadow later departures.
                            if (departure["m_HasExitTime"]?.AsBool() != true) break;
                        }
                        current = machine;
                    }
                    var shapes = ActiveShapes(Clip(state?["m_Motion"]), requireConstant: true);
                    // Even a constant zero curve in another contributing layer can override
                    // this expression. Partial/gated layers also compete despite not being
                    // eligible to supply a permanent driver themselves.
                    if (HasCompetingBindings(shapes)) return new();
                    return shapes;
                }

            }

            HashSet<(string, string)> LayerBindings(YamlNode animatorLayer)
            {
                var bindings = new HashSet<(string, string)>();
                foreach (long id in graph.Reachable(animatorLayer["m_StateMachine"]?.FileID ?? 0))
                    if (controller.Doc(id) is { ClassId: 1102 } state)
                        bindings.UnionWith(MotionBindings(state.Root?["m_Motion"], out _));
                return bindings;
            }

            HashSet<(string, string)> MotionBindings(YamlNode motion, out bool complete)
            {
                var bindings = new HashSet<(string, string)>();
                var active = new HashSet<(string, long)>();
                complete = VisitMotion(motion, controller, asset.Guid);
                return bindings;
                bool VisitMotion(YamlNode reference, UnityScene owner, string ownerGuid)
                {
                    if ((reference?.FileID ?? 0) == 0) return true;
                    var clip = Clip(reference);
                    foreach (var curve in clip?["m_FloatCurves"]?.Seq ?? new())
                    {
                        string attribute = curve["attribute"]?.AsString();
                        if (curve["classID"]?.AsInt() == 137 && curve["path"]?.AsString() is string path &&
                            attribute?.StartsWith("blendShape.", StringComparison.Ordinal) == true)
                            bindings.Add((path, attribute["blendShape.".Length..]));
                    }
                    if (clip != null) return true;
                    if (reference.Guid != null)
                    {
                        var source = package.ByGuid(reference.Guid);
                        if (source?.HasContent != true || source.Extension is not (".asset" or ".controller")) return false;
                        owner = package.ReadScene(source); ownerGuid = source.Guid;
                    }
                    long id = reference.FileID.Value;
                    var tree = owner.Doc(id);
                    if (tree?.ClassId != 206 || !active.Add((ownerGuid, id))) return false;
                    bool resolved = true;
                    foreach (var child in tree.Root["m_Childs"]?.Seq ?? tree.Root["m_Children"]?.Seq ?? new())
                        resolved &= VisitMotion(child["m_Motion"], owner, ownerGuid);
                    active.Remove((ownerGuid, id));
                    return resolved;
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
        bool useNeutral = blinkLoops.Count == 0 && blinks.Count == 0 && neutralBlinks.Count == 1;
        if (avatar.Blink == null && (blinkLoops.Count == 1 || useNeutral))
        {
            var loop = useNeutral ? neutralBlinks[0] : blinkLoops[0];
            avatar.Blink = loop.Bindings[0];
            avatar.AdditionalBlinks.AddRange(loop.Bindings.Skip(1));
            foreach (var entry in loop.InitialValues) avatar.BlinkInitialValues.Add(entry.Key, entry.Value);
            UniLog.Log((useNeutral ? "Animator neutral blink fallback: " : "Animator blink loop: ") + string.Join(", ", loop.Bindings.Select(b =>
                $"{b.MeshGameObjectPath}/{b.BlendShapeName} -> {b.Preset}")) +
                $"; {loop.InitialValues.Count} open/reset values. Animator timing and parameter gates are replaced by native blinking.");
        }
        else if (avatar.Blink == null && blinkLoops.Count == 0 && blinks.Count == 1)
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
            // SDK proxy_hands_idle contains only humanoid hand-muscle curves. Packages
            // commonly omit SDK assets. Recognize this exact missing reference, never
            // an arbitrary missing motion or a packaged asset reusing the same GUID.
            if (asset?.HasContent != true && motion.FileID == 7400000 &&
                guid.Equals("14980fc5fe40191418954549174fe63e", StringComparison.OrdinalIgnoreCase))
                return clips[guid] = UnityYaml.ParseFlatDocument("m_FloatCurves: []");
            YamlNode clip = asset?.Extension == ".anim" && asset.HasContent
                ? package.ReadScene(asset).Doc(motion.FileID ?? 7400000)?.Root : null;
            clips[guid] = clip;
            return clip;
        }
    }

    private static List<Shape> ActiveShapes(YamlNode clip, bool requireConstant = false, bool requireOpen = false)
    {
        var result = new List<Shape>();
        foreach (YamlNode curve in clip?["m_FloatCurves"]?.Seq ?? new())
        {
            string attribute = curve["attribute"]?.AsString();
            string path = curve["path"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true ||
                path == null) continue;
            var keys = curve["curve"]?["m_Curve"]?.Seq;
            // A looping closed-eye pose must not become a driver that opens the eyes.
            // Require an authored fully open value as well as the closed peak.
            if (requireOpen && (keys == null || keys.Count == 0 ||
                keys.Any(key => !float.IsFinite(key["value"]?.AsFloat() ?? 0)) ||
                MathF.Abs(keys.Min(key => key["value"]?.AsFloat() ?? 0)) >= 0.01f)) return new();
            if (requireConstant)
            {
                // Peak weights describe a blink, but a permanent viseme must retain its
                // value throughout the clip. Equal endpoints alone do not rule out a
                // tangent-driven excursion between keys (including on neutral curves).
                if (keys == null || keys.Count == 0) return new();
                float value = keys[0]["value"]?.AsFloat() ?? 0;
                if (!float.IsFinite(value) || keys.Any(key => (key["value"]?.AsFloat() ?? 0) != value)) return new();
                for (int i = 0; i + 1 < keys.Count; i++)
                    if ((keys[i]["outSlope"]?.AsFloat() ?? 0) != 0 ||
                        (keys[i + 1]["inSlope"]?.AsFloat() ?? 0) != 0) return new();
            }
            float peak = keys?.Select(key => key["value"]?.AsFloat() ?? 0).DefaultIfEmpty().Max() ?? 0;
            // Neutral curves are requirements too: prefab/FBX initial weights may
            // be nonzero. Keep every face curve
            // so the single-shape check rejects motions requiring additional resets.
            result.Add(new Shape(path, attribute["blendShape.".Length..], peak));
        }
        // A face driver cannot reproduce visibility authored alongside the shape.
        // Even constant enable curves may differ from the prefab's initial state.
        foreach (YamlNode curve in clip?["m_FloatCurves"]?.Seq ?? new())
        {
            string path = curve["path"]?.AsString();
            if (path == null) continue;
            string attribute = curve["attribute"]?.AsString();
            int classId = curve["classID"]?.AsInt() ?? 0;
            if (result.Any(shape =>
                (classId == 137 && attribute == "m_Enabled" && path == shape.Renderer) ||
                (classId == 1 && attribute == "m_IsActive" &&
                    (path.Length == 0 || path == shape.Renderer || shape.Renderer.StartsWith(path + "/", StringComparison.Ordinal)))))
                return new();
        }
        return result;
    }
}
