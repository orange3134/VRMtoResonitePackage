using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Imports explicit face curves and a validated Animator subset; never guesses poses by name.</summary>
public static class VrchatExpressionParser
{
    public static ExpressionModel Parse(UnityPackage package, YamlNode descriptor)
    {
        var model = new ExpressionModel();
        var clips = new Dictionary<string, ExpressionClip>(StringComparer.Ordinal);
        var expressionClips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var playable in descriptor?["baseAnimationLayers"]?.Seq ?? new())
        {
            if (playable["isDefault"]?.AsBool() == true || playable["type"]?.AsInt() != 5) continue;
            var asset = package.ByGuid(playable["animatorController"]?.Guid);
            if (asset == null || !asset.HasContent) continue;
            if (asset.Extension != ".controller") { Warn($"{asset.LogicalPath}: unsupported controller type"); continue; }
            var scene = package.ReadScene(asset);
            var controller = scene.Documents.Values.FirstOrDefault(d => d.ClassId == 91)?.Root;
            foreach (var p in controller?["m_AnimatorParameters"]?.Seq ?? new())
            {
                string name = p["m_Name"]?.AsString();
                int type = p["m_Type"]?.AsInt() ?? 1;
                if (string.IsNullOrEmpty(name)) continue;
                float value = type switch { 3 => p["m_DefaultInt"]?.AsInt() ?? 0,
                    4 => p["m_DefaultBool"]?.AsBool() == true ? 1 : 0, _ => p["m_DefaultFloat"]?.AsFloat() ?? 0 };
                model.Parameters.TryAdd(name, new(name, type, value));
            }
            int index = 0;
            foreach (var layerNode in controller?["m_AnimatorLayers"]?.Seq ?? new())
            {
                int order = index++;
                string label = asset.LogicalPath + ": " + layerNode["m_Name"]?.AsString();
                var layer = new ExpressionLayer { Id = asset.Guid + ":" + order,
                    Name = layerNode["m_Name"]?.AsString() ?? "Layer",
                    Weight = order == 0 ? 1 : layerNode["m_DefaultWeight"]?.AsFloat() ?? 0 };
                var errors = new List<string>();
                if (layer.Weight == 0) continue;
                if (!float.IsFinite(layer.Weight) || layer.Weight < 0 || layer.Weight > 1) errors.Add("invalid layer weight");
                if (layerNode["m_BlendingMode"]?.AsInt() == 1) errors.Add("additive layer");
                if ((layerNode["m_Mask"]?.FileID ?? 0) != 0) errors.Add("AvatarMask");
                if ((layerNode["m_SyncedLayerIndex"]?.AsInt(-1) ?? -1) >= 0) errors.Add("synced layer");
                var machine = scene.Doc(layerNode["m_StateMachine"]?.FileID ?? 0)?.Root;
                if (machine == null) { Warn(label + ": missing state machine"); continue; }
                if ((machine["m_ChildStateMachines"]?.Seq?.Count ?? 0) != 0) errors.Add("nested state machine");
                if ((machine["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0) errors.Add("state machine behaviour");
                // Follow all structural routes. Viseme-only reachability pruning is inappropriate here.
                var reachable = new HashSet<long>();
                Visit(machine["m_DefaultState"]?.FileID ?? 0);
                foreach (string key in new[] { "m_EntryTransitions", "m_AnyStateTransitions" })
                    foreach (var r in VrchatAnimatorDefaults.ActiveTransitions(scene, machine[key]?.Seq))
                        VisitTransition(r);
                var ids = (machine["m_ChildStates"]?.Seq ?? new()).Select(s => s["m_State"]?.FileID ?? 0)
                    .Where(reachable.Contains).Distinct().ToList();
                foreach (long id in ids)
                {
                    var state = scene.Doc(id)?.Root;
                    var motion = state?["m_Motion"];
                    ExpressionClip clip = ReadClip(motion);
                    if ((motion?.FileID ?? 0) != 0 && clip == null) errors.Add("unsupported motion in " + state?["m_Name"]?.AsString());
                    if ((state?["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0) errors.Add("state behaviour");
                    if (new[] { "m_SpeedParameterActive", "m_TimeParameterActive", "m_CycleOffsetParameterActive", "m_MirrorParameterActive" }
                        .Any(k => state?[k]?.AsBool() == true) || (state?["m_CycleOffset"]?.AsFloat() ?? 0) != 0)
                        errors.Add("parameterized playback or cycle offset");
                    float speed = state?["m_Speed"]?.AsFloat(1) ?? 1;
                    if (!float.IsFinite(speed) || speed <= 0) errors.Add("non-positive playback speed");
                    layer.States.Add(new(state?["m_Name"]?.AsString() ?? "State", clip?.Id, speed,
                        state?["m_WriteDefaultValues"]?.AsBool() == true));
                }
                layer.DefaultState = ids.IndexOf(machine["m_DefaultState"]?.FileID ?? 0);
                if (layer.DefaultState < 0 || layer.States.Count == 0) errors.Add("missing default state");
                AddTransitions(machine["m_EntryTransitions"]?.Seq, -1, layer.Entry);
                AddTransitions(machine["m_AnyStateTransitions"]?.Seq, -1, layer.Transitions);
                for (int i = 0; i < ids.Count; i++) AddTransitions(scene.Doc(ids[i])?.Root?["m_Transitions"]?.Seq, i, layer.Transitions);
                var usedParameters = layer.Entry.Concat(layer.Transitions).SelectMany(t => t.Conditions).Select(c => c.Parameter).ToHashSet();
                var independentClips = new HashSet<string>();
                Collect(machine, new HashSet<long>());
                // Leave autonomous blink/viseme layers to their existing dedicated drivers.
                bool expressionLayer = usedParameters.Any(p => p != "Viseme" && p != "Voice") && independentClips.Count > 0;
                if (!expressionLayer) continue;
                expressionClips.UnionWith(independentClips);
                if (layer.States.Select(s => s.WriteDefaults).Distinct().Count() > 1) errors.Add("mixed Write Defaults");
                if (layer.States.Any(s => !s.WriteDefaults))
                {
                    var bindingSets = layer.States.Select(s => s.ClipId == null ? new HashSet<string>() :
                        clips[s.ClipId].Curves.Select(c => c.Binding.Key).ToHashSet()).ToArray();
                    if (bindingSets.Skip(1).Any(s => !s.SetEquals(bindingSets[0]))) errors.Add("history-dependent unanimated properties");
                }
                if (errors.Count == 0) model.Layers.Add(layer);
                else Warn(label + ": automatic layer omitted (" + string.Join(", ", errors.Distinct()) + "); supported clips remain directly selectable");

                void Collect(YamlNode stateMachine, HashSet<long> visited)
                {
                    foreach (var child in stateMachine?["m_ChildStates"]?.Seq ?? new())
                    {
                        long id = child["m_State"]?.FileID ?? 0;
                        if (!visited.Add(id)) continue;
                        var state = scene.Doc(id)?.Root;
                        CollectMotion(state?["m_Motion"], visited);
                        CollectConditions(state?["m_Transitions"]?.Seq);
                    }
                    CollectConditions(stateMachine?["m_AnyStateTransitions"]?.Seq);
                    CollectConditions(stateMachine?["m_EntryTransitions"]?.Seq);
                    foreach (var child in stateMachine?["m_ChildStateMachines"]?.Seq ?? new())
                    {
                        long id = child["m_StateMachine"]?.FileID ?? 0;
                        if (visited.Add(id)) Collect(scene.Doc(id)?.Root, visited);
                    }
                }
                void CollectMotion(YamlNode motion, HashSet<long> visited)
                {
                    if (motion?.Guid is not null)
                    {
                        var independent = ReadClip(motion);
                        if (independent != null) independentClips.Add(independent.Id);
                    }
                    else if ((motion?.FileID ?? 0) is long id && id != 0 && visited.Add(id))
                        foreach (var child in scene.Doc(id)?.Root?["m_Childs"]?.Seq ?? new()) CollectMotion(child["m_Motion"], visited);
                }
                void CollectConditions(IEnumerable<YamlNode> references)
                {
                    foreach (var reference in VrchatAnimatorDefaults.ActiveTransitions(scene, references))
                        foreach (var condition in scene.Doc(reference.FileID ?? 0)?.Root?["m_Conditions"]?.Seq ?? new())
                            if (condition["m_ConditionEvent"]?.AsString() is string parameter) usedParameters.Add(parameter);
                }

                void Visit(long id)
                {
                    if (id == 0 || !reachable.Add(id)) return;
                    foreach (var r in VrchatAnimatorDefaults.ActiveTransitions(scene, scene.Doc(id)?.Root?["m_Transitions"]?.Seq)) VisitTransition(r);
                }
                void VisitTransition(YamlNode r)
                {
                    var t = scene.Doc(r.FileID ?? 0)?.Root;
                    Visit(t?["m_DstState"]?.FileID ?? 0);
                    if ((t?["m_DstStateMachine"]?.FileID ?? 0) != 0) errors.Add("state machine destination");
                }
                void AddTransitions(IEnumerable<YamlNode> references, int source, List<ExpressionTransition> target)
                {
                    foreach (var r in VrchatAnimatorDefaults.ActiveTransitions(scene, references))
                    {
                        var t = scene.Doc(r.FileID ?? 0)?.Root;
                        int dst = ids.IndexOf(t?["m_DstState"]?.FileID ?? 0);
                        if (dst < 0 || t?["m_IsExit"]?.AsBool() == true) { errors.Add("exit or unresolved transition"); continue; }
                        if ((t["m_InterruptionSource"]?.AsInt() ?? 0) != 0) errors.Add("transition interruption");
                        var transition = new ExpressionTransition { Source = source, Destination = dst,
                            CanTransitionToSelf = t["m_CanTransitionToSelf"]?.AsBool() == true,
                            HasExitTime = t["m_HasExitTime"]?.AsBool() == true, ExitTime = t["m_ExitTime"]?.AsFloat() ?? 0,
                            Duration = Math.Max(0, t["m_TransitionDuration"]?.AsFloat() ?? 0),
                            FixedDuration = t["m_HasFixedDuration"]?.AsBool() == true, Offset = t["m_TransitionOffset"]?.AsFloat() ?? 0 };
                        if (!float.IsFinite(transition.Duration) || !float.IsFinite(transition.ExitTime) || !float.IsFinite(transition.Offset))
                            errors.Add("invalid transition timing");
                        foreach (var c in t["m_Conditions"]?.Seq ?? new())
                        {
                            string parameter = c["m_ConditionEvent"]?.AsString();
                            int mode = c["m_ConditionMode"]?.AsInt() ?? 0;
                            if (parameter == null || mode is not (1 or 2 or 3 or 4 or 6 or 7)) { errors.Add("unknown condition"); continue; }
                            float threshold = c["m_EventTreshold"]?.AsFloat() ?? 0;
                            if (!float.IsFinite(threshold)) errors.Add("invalid condition threshold");
                            transition.Conditions.Add(new(parameter, mode, threshold));
                        }
                        target.Add(transition);
                    }
                }
            }
        }
        // VRC expression parameter defaults override controller defaults.
        var parameterAsset = package.ByGuid(descriptor?["expressionParameters"]?.Guid);
        if (parameterAsset?.HasContent == true)
        {
            var root = package.ReadScene(parameterAsset).Documents.Values.FirstOrDefault(d => d.Root?["parameters"] != null)?.Root;
            foreach (var p in root?["parameters"]?.Seq ?? new())
            {
                string name = p["name"]?.AsString();
                if (string.IsNullOrEmpty(name)) continue;
                int type = p["valueType"]?.AsInt() switch { 0 => 3, 2 => 4, _ => 1 };
                model.Parameters[name] = new(name, type, p["defaultValue"]?.AsFloat() ?? 0, p["saved"]?.AsBool() == true);
            }
        }
        foreach (string hand in new[] { "Left", "Right" })
        {
            model.Parameters["Gesture" + hand] = new("Gesture" + hand, 3, 0);
            model.Parameters["Gesture" + hand + "Weight"] = new("Gesture" + hand + "Weight", 1, 0);
        }
        ReadMenu(descriptor?["expressionsMenu"]?.Guid, model.Menu, new());
        model.Clips.AddRange(expressionClips.Select(id => clips[id]));
        UniLog.Log($"Expression import: {model.Clips.Count} clips, {model.Layers.Count} layers, {model.Menu.Count} menu controls");
        return model;

        void Warn(string message) { model.Diagnostics.Add(message); UniLog.Warning("Expressions: " + message); }
        ExpressionClip ReadClip(YamlNode reference)
        {
            string guid = reference?.Guid;
            if (guid == null || (reference.FileID ?? 0) == 0) return null;
            string key = guid + ":" + reference.FileID;
            if (clips.TryGetValue(key, out var cached)) return cached;
            clips[key] = null;
            var asset = package.ByGuid(guid);
            if (asset?.Extension != ".anim" || !asset.HasContent) return null;
            var root = package.ReadScene(asset).Doc(reference.FileID ?? 7400000)?.Root;
            if (root == null) return null;
            var clip = new ExpressionClip { Id = key, Name = root["m_Name"]?.AsString() ?? "Expression",
                Source = asset.LogicalPath, Duration = root["m_AnimationClipSettings"]?["m_StopTime"]?.AsFloat() ?? 0,
                Loop = root["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() == true };
            bool unsupported = new[] { "m_RotationCurves", "m_CompressedRotationCurves", "m_EulerCurves", "m_PositionCurves", "m_ScaleCurves", "m_PPtrCurves" }
                .Any(k => (root[k]?.Seq?.Count ?? 0) != 0);
            unsupported |= !float.IsFinite(clip.Duration) || clip.Duration < 0 ||
                (root["m_AnimationClipSettings"]?["m_StartTime"]?.AsFloat() ?? 0) != 0 || (root["m_Events"]?.Seq?.Count ?? 0) > 0;
            foreach (var c in root["m_FloatCurves"]?.Seq ?? new())
            {
                string attribute = c["attribute"]?.AsString(), path = c["path"]?.AsString();
                if (c["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true || path == null)
                { unsupported = true; continue; }
                var curve = new ExpressionCurve { Binding = new(path, attribute[11..]) };
                foreach (var k in c["curve"]?["m_Curve"]?.Seq ?? new())
                {
                    if ((k["weightedMode"]?.AsInt() ?? 0) != 0) unsupported = true;
                    curve.Keys.Add(new(k["time"]?.AsFloat() ?? 0, (k["value"]?.AsFloat() ?? 0) / 100,
                        (k["inSlope"]?.AsFloat() ?? 0) / 100, (k["outSlope"]?.AsFloat() ?? 0) / 100));
                }
                if (curve.Keys.Count == 0 || curve.Keys.Any(k => !float.IsFinite(k.Time) || !float.IsFinite(k.Value) || float.IsNaN(k.InSlope) || float.IsNaN(k.OutSlope)) ||
                    curve.Keys.Zip(curve.Keys.Skip(1)).Any(pair => pair.First.Time >= pair.Second.Time)) unsupported = true;
                else { clip.Duration = Math.Max(clip.Duration, curve.Keys[^1].Time); clip.Curves.Add(curve); }
            }
            if (clip.Curves.Select(c => c.Binding.Key).Distinct().Count() != clip.Curves.Count) unsupported = true;
            if (unsupported) { Warn(asset.LogicalPath + ": unsupported or invalid animation tracks; clip omitted"); return null; }
            clips[key] = clip;
            return clip;
        }
        void ReadMenu(string guid, List<ExpressionMenuControl> target, HashSet<string> path)
        {
            if (guid == null) return;
            if (!path.Add(guid)) { Warn("cyclic expression menu " + guid); return; }
            var asset = package.ByGuid(guid);
            if (asset?.HasContent != true) { Warn("missing expression menu " + guid); path.Remove(guid); return; }
            var root = package.ReadScene(asset).Documents.Values.FirstOrDefault(d => d.Root?["controls"] != null)?.Root;
            foreach (var c in root?["controls"]?.Seq ?? new())
            {
                int serializedType = c["type"]?.AsInt() ?? 0;
                int type = serializedType switch { 101 => 0, 102 => 1, 103 => 2, _ => -1 };
                var control = new ExpressionMenuControl { Name = c["name"]?.AsString() ?? "Control", Type = type,
                    Parameter = c["parameter"]?["name"]?.AsString(), Value = c["value"]?.AsFloat() ?? 0 };
                if (type is not (0 or 1 or 2)) { Warn(asset.LogicalPath + ": Puppet control requires a continuous-input adapter: " + control.Name); continue; }
                if (type == 2) ReadMenu(c["subMenu"]?.Guid, control.Children, path);
                target.Add(control);
            }
            path.Remove(guid);
        }
    }
}
