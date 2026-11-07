using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Reads FaceEmo candidates and supplies graphs for Catalog-constrained gesture routing.</summary>
public static class VrchatExpressionParser
{
    public static ExpressionModel Parse(UnityPackage package, YamlNode descriptor, IEnumerable<YamlNode> modularComponents = null)
    {
        var model = new ExpressionModel { DetectedExpressions = new(), ImportedPatterns = new() };
        var clips = new Dictionary<string, ExpressionClip>(StringComparer.Ordinal);
        var importer = new VrchatFaceEmoExpressionImporter(modularComponents, ReadClip);
        var controllers = new List<(UnityScene Scene, string Id)>();
        foreach (var playable in descriptor?["baseAnimationLayers"]?.Seq ?? new())
        {
            if (playable["isDefault"]?.AsBool() == true || playable["type"]?.AsInt() != 5) continue;
            var asset = package.ByGuid(playable["animatorController"]?.Guid);
            if (asset?.HasContent != true) continue;
            if (asset.Extension != ".controller") { Warn(asset.LogicalPath + ": unsupported controller type"); continue; }
            var scene = package.ReadScene(asset);
            controllers.Add((scene, asset.Guid + ":" + controllers.Count));
        }
        foreach (var (scene, id) in controllers)
        {
            importer.Read(model.ImportedPatterns, scene, scene.Documents.Values.FirstOrDefault(d => d.ClassId == 91)?.Root,
                id);
            if (model.ImportedPatterns.Cac) break;
        }
        if (!model.ImportedPatterns.Cac) model.ImportedPatterns.GestureRouting = new(controllers, SafeRoutingMotion);
        foreach (string hand in new[] { "Left", "Right" })
        {
            model.Parameters["Gesture" + hand] = new("Gesture" + hand, 3, 0);
            model.Parameters["Gesture" + hand + "Weight"] = new("Gesture" + hand + "Weight", 1, 0);
        }
        model.Clips.AddRange(clips.Values.Where(c => c != null));
        VrchatFaceEmoExpressionImporter.SelectFirstSet(model);
        UniLog.Log($"Expression import (FaceEmo): {model.Clips.Count} source clips; first set finalized after face renderer resolution");
        return model;

        void Warn(string message) { model.Diagnostics.Add(message); UniLog.Warning("Expressions: " + message); }
        bool SafeRoutingMotion(YamlNode reference)
        {
            var asset = package.ByGuid(reference?.Guid);
            if (asset?.HasContent != true || asset.Extension != ".anim" ||
                package.ReadScene(asset).Doc(reference.FileID ?? 0) is not { ClassId: 74 } doc) return false;
            var root = doc.Root;
            return root["m_Compressed"]?.AsBool() != true && (root["m_Events"]?.Seq?.Count ?? 0) == 0 &&
                !(root["m_FloatCurves"]?.Seq?.Any(c => c["classID"]?.AsInt() == 95) ?? false);
        }
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
            var clip = new ExpressionClip { Id = key, Name = root["m_Name"]?.AsString() ?? "Expression", Source = asset.LogicalPath };
            bool invalid = false;
            // FaceEmo extracts the face animation regardless of unrelated tracks, events, playback or state behaviours.
            // ResoPon exports fixed face poses only: none of those unrelated effects execute.
            foreach (var c in root["m_FloatCurves"]?.Seq ?? new())
            {
                string attribute = c["attribute"]?.AsString(), path = c["path"]?.AsString();
                if (c["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true) continue;
                if (path == null || attribute.Length == 11) { invalid = true; continue; }
                if (VrchatExpressionDetection.IsViseme(attribute[11..])) continue;
                var curve = new ExpressionCurve { Binding = new(path, attribute[11..]) };
                foreach (var k in c["curve"]?["m_Curve"]?.Seq ?? new())
                    curve.Keys.Add(new(k["time"]?.AsFloat() ?? 0, (k["value"]?.AsFloat() ?? 0) / 100,
                        (k["inSlope"]?.AsFloat() ?? 0) / 100, (k["outSlope"]?.AsFloat() ?? 0) / 100));
                if (curve.Keys.Count == 0) continue;
                if (curve.Keys.Any(k => !float.IsFinite(k.Time) || !float.IsFinite(k.Value) || float.IsNaN(k.InSlope) || float.IsNaN(k.OutSlope)) ||
                    curve.Keys.Zip(curve.Keys.Skip(1)).Any(pair => pair.First.Time >= pair.Second.Time) ||
                    !UnityWeightedExpressionCurve.TryBake(curve, c["curve"]["m_Curve"].Seq)) invalid = true;
                else { clip.Duration = Math.Max(clip.Duration, curve.Keys[^1].Time); clip.Curves.Add(curve); }
            }
            if (invalid || clip.Curves.Select(c => c.Binding.Key).Distinct().Count() != clip.Curves.Count)
            { Warn(asset.LogicalPath + ": invalid face animation curves; clip omitted"); return null; }
            if (clip.Curves.Count == 0) return null;
            clips[key] = clip;
            return clip;
        }
    }
}