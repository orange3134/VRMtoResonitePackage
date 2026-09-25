using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>FaceEmo-style candidate discovery, independent of validated Animator routing.</summary>
internal sealed class VrchatExpressionDetection
{
    private readonly HashSet<string> _contacts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _physics = new(StringComparer.Ordinal);
    private static readonly string[] Visemes = { "sil", "pp", "ff", "th", "dd", "kk", "ch", "ss", "nn", "rr", "aa", "e", "ih", "oh", "ou" };

    public VrchatExpressionDetection(IEnumerable<YamlNode> components)
    {
        foreach (var component in components ?? Enumerable.Empty<YamlNode>())
        {
            string parameter = component["parameter"]?.AsString();
            if (string.IsNullOrEmpty(parameter)) continue;
            if (component["receiverType"] != null && component["collisionTags"] != null)
                _contacts.Add(parameter);
            if (component["m_Script"]?.Guid == VrchatConstants.PhysBoneDllGuid &&
                component["m_Script"]?.FileID == VrchatConstants.PhysBoneScriptFileId)
                foreach (string suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" })
                    _physics.Add(parameter + suffix);
        }
    }

    internal static bool IsViseme(string name) => Visemes.Any(v =>
        name.Contains("vrc.v_" + v, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ExpressionClip> Collect(UnityScene scene, YamlNode controller, Func<YamlNode, ExpressionClip> read)
    {
        var result = new Dictionary<string, ExpressionClip>(StringComparer.Ordinal);
        var visited = new HashSet<long>();
        foreach (var layer in controller?["m_AnimatorLayers"]?.Seq ?? new())
            Machine(layer["m_StateMachine"]?.FileID ?? 0);
        return result.Values;

        void Machine(long id)
        {
            if (id == 0 || !visited.Add(id)) return;
            var machine = scene.Doc(id)?.Root;
            State(machine?["m_DefaultState"]?.FileID ?? 0, Array.Empty<YamlNode>());
            Transitions(machine?["m_EntryTransitions"]?.Seq);
            Transitions(machine?["m_AnyStateTransitions"]?.Seq);
            foreach (var child in machine?["m_ChildStates"]?.Seq ?? new())
                Transitions(scene.Doc(child["m_State"]?.FileID ?? 0)?.Root?["m_Transitions"]?.Seq);
            foreach (var child in machine?["m_ChildStateMachines"]?.Seq ?? new())
                Machine(child["m_StateMachine"]?.FileID ?? 0);
        }
        void Transitions(IEnumerable<YamlNode> references)
        {
            foreach (var reference in references ?? Enumerable.Empty<YamlNode>())
            {
                var transition = scene.Doc(reference.FileID ?? 0)?.Root;
                if (transition == null || transition["m_Mute"]?.AsBool() == true) continue;
                State(transition["m_DstState"]?.FileID ?? 0,
                    transition["m_Conditions"]?.Seq?.ToArray() ?? Array.Empty<YamlNode>());
            }
        }
        void State(long id, YamlNode[] conditions)
        {
            if (scene.Doc(id) is not { ClassId: 1102 } document) return;
            var state = document.Root;
            bool gesture = conditions.Any(c => c["m_ConditionEvent"]?.AsString() is "GestureLeft" or "GestureRight" &&
                c["m_ConditionMode"]?.AsInt() == 6);
            if (!gesture && (conditions.Length > 0 && conditions.All(c => c["m_ConditionMode"]?.AsInt() is 1 or 2) ||
                conditions.Any(c => _contacts.Contains(c["m_ConditionEvent"]?.AsString() ?? "") ||
                    _physics.Contains(c["m_ConditionEvent"]?.AsString() ?? "")) ||
                state["m_TimeParameterActive"]?.AsBool() == true && _physics.Contains(state["m_TimeParameter"]?.AsString() ?? ""))) return;
            bool fist = state["m_TimeParameterActive"]?.AsBool() == true && conditions.Any(c =>
                c["m_ConditionEvent"]?.AsString() is "GestureLeft" or "GestureRight" &&
                c["m_ConditionMode"]?.AsInt() == 6 && c["m_EventTreshold"]?.AsFloat() == 1);
            Motion(state["m_Motion"], new(), fist);
        }
        void Motion(YamlNode motion, HashSet<long> trees, bool includeFirst = false)
        {
            if (motion?.Guid != null)
            {
                var clip = read(motion);
                if (clip?.Curves.Count > 0 && clip.Curves.Any(c => !IsViseme(c.Binding.Shape))) result.TryAdd(clip.Id, clip);
                return;
            }
            long id = motion?.FileID ?? 0;
            if (id == 0 || !trees.Add(id) || scene.Doc(id) is not { ClassId: 206 } tree) return;
            var children = tree.Root?["m_Childs"]?.Seq;
            if (children?.Count > 0)
            {
                // Ordinary expressions use the last child; fist motion time also exposes the base endpoint.
                if (includeFirst) Motion(children[0]["m_Motion"], trees);
                Motion(children[^1]["m_Motion"], trees);
            }
        }
    }

    public static void FilterFaceCurves(ExpressionModel model, IReadOnlyDictionary<ExpressionBinding, float> values)
    {
        if (model.DetectedExpressions == null) return;
        foreach (var clip in model.Clips) clip.Curves.RemoveAll(c => !values.ContainsKey(c.Binding));
        if (model.ImportedGestureSets != null) VrchatCacExpressionImporter.SelectFirstSet(model, pruneClips: true);
        var candidates = model.DetectedExpressions.Select(c => c.Id).ToHashSet();
        model.DetectedExpressions.Clear();
        foreach (var clip in model.Clips)
        {
            if (!candidates.Contains(clip.Id)) continue;
            var difference = new ExpressionClip { Id = clip.Id, Name = clip.Name, Source = clip.Source,
                Duration = clip.Duration, Loop = clip.Loop };
            difference.Curves.AddRange(clip.Curves.Where(c => c.Keys.Any(k => !Approximately(k.Value, values[c.Binding]))));
            if (difference.Curves.Count > 0) model.DetectedExpressions.Add(difference);
        }
        string message = $"Face expression detection: {model.DetectedExpressions.Count} candidates with non-viseme face differences; " +
            "complete face reset curves retained for validated Animator routes.";
        model.Diagnostics.Add(message); UniLog.Log(message);
    }

    // Unity Mathf.Approximately, applied in the authored 0..100 weight range.
    private static bool Approximately(float a, float b) =>
        MathF.Abs((a - b) * 100) < MathF.Max(0.000001f * MathF.Max(MathF.Abs(a * 100), MathF.Abs(b * 100)), float.Epsilon * 8);
}
