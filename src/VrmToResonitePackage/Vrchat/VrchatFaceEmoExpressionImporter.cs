using Elements.Core;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

// Source branches are retained until renderer identity and authored weights are available.
// They describe FaceEmo patterns, not an executable Unity Animator graph.
internal sealed class FaceEmoPatterns
{
    public bool Cac;
    public List<FaceEmoPattern> Patterns = new();
}
internal sealed class FaceEmoPattern
{
    public string Id, Name;
    public List<FaceEmoBranch> Branches = new();
}
internal sealed class FaceEmoBranch
{
    public string Name;
    public ExpressionClip Motion, BaseMotion;
    public bool Grip, BaseAtEnd;
    public List<ExpressionClip> FaceMotions = new();
    public List<ExpressionCondition> Conditions = new();
}

/// <summary>FaceEmo ImportNormal/ImportCac and first-pattern selection for discrete hand signs.</summary>
internal sealed class VrchatFaceEmoExpressionImporter
{
    private readonly HashSet<string> _contacts = new(StringComparer.Ordinal), _physics = new(StringComparer.Ordinal);
    private readonly Func<YamlNode, ExpressionClip> _readClip;
    public VrchatFaceEmoExpressionImporter(IEnumerable<YamlNode> components, Func<YamlNode, ExpressionClip> readClip)
    {
        _readClip = readClip;
        foreach (var component in components ?? Enumerable.Empty<YamlNode>())
        {
            string parameter = component["parameter"]?.AsString();
            if (string.IsNullOrEmpty(parameter)) continue;
            if (component["receiverType"] != null && component["collisionTags"] != null) _contacts.Add(parameter);
            if (component["m_Script"]?.Guid == VrchatConstants.PhysBoneDllGuid &&
                component["m_Script"]?.FileID == VrchatConstants.PhysBoneScriptFileId)
                foreach (string suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" }) _physics.Add(parameter + suffix);
        }
    }

    public void Read(FaceEmoPatterns result, UnityScene scene, YamlNode controller, string id)
    {
        if (result.Cac) return;
        var layers = controller?["m_AnimatorLayers"]?.Seq ?? new();
        // FaceEmo prefers the first CAC layer to every ordinary layer.
        foreach (var layer in layers)
        {
            var root = scene.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root;
            var entries = (root?["m_ChildStateMachines"]?.Seq ?? new())
                .SelectMany(c => scene.Doc(c["m_StateMachine"]?.FileID ?? 0)?.Root?["m_EntryTransitions"]?.Seq ?? new())
                .Select(r => scene.Doc(r.FileID ?? 0)?.Root).Where(t => t != null).ToArray();
            if (!entries.Any(t => t["m_Conditions"]?.Seq?.FirstOrDefault()?["m_ConditionEvent"]?.AsString() == "SYNC_EM_EMOTE")) continue;
            result.Cac = true; result.Patterns.Clear();
            var indexed = new List<(int Index, FaceEmoBranch Branch)>();
            foreach (var entry in entries)
            {
                var conditions = entry["m_Conditions"]?.Seq;
                if (entry["m_Mute"]?.AsBool() == true || conditions?.Count != 1 ||
                    conditions[0]["m_ConditionEvent"]?.AsString() != "SYNC_EM_EMOTE" || conditions[0]["m_ConditionMode"]?.AsInt() != 6) continue;
                float number = conditions[0]["m_EventTreshold"]?.AsFloat() ?? 0;
                if (!float.IsFinite(number) || number < 1 || number >= int.MaxValue || number != MathF.Truncate(number)) continue;
                int index = (int)number - 1;
                var condition = new ExpressionCondition(index % 14 < 7 ? "GestureRight" : "GestureLeft", 6, index % 7 + 1);
                var branch = State(scene, entry["m_DstState"]?.FileID ?? 0, new() { condition });
                if (branch != null) indexed.Add((index, branch));
            }
            foreach (var group in indexed.OrderBy(b => b.Index).GroupBy(b => b.Index / 14))
                result.Patterns.Add(new() { Id = id + ":cac:" + group.Key, Name = "FaceEmo expression set " + (group.Key + 1),
                    Branches = group.Select(b => b.Branch).ToList() });
            return;
        }
        int indexLayer = 0;
        foreach (var layer in layers)
        {
            var branches = Machine(layer["m_StateMachine"]?.FileID ?? 0, new());
            result.Patterns.Add(new() { Id = id + ":" + indexLayer++, Name = layer["m_Name"]?.AsString(), Branches = branches });
        }

        List<FaceEmoBranch> Machine(long fileId, HashSet<long> path)
        {
            var branches = new List<FaceEmoBranch>();
            if (fileId == 0 || !path.Add(fileId)) return branches;
            var machine = scene.Doc(fileId)?.Root;
            var defaultBranch = State(scene, machine?["m_DefaultState"]?.FileID ?? 0, new());
            if (defaultBranch != null) branches.Add(defaultBranch);
            foreach (string key in new[] { "m_EntryTransitions", "m_AnyStateTransitions" }) Transitions(machine?[key]?.Seq);
            foreach (var child in machine?["m_ChildStates"]?.Seq ?? new())
                Transitions(scene.Doc(child["m_State"]?.FileID ?? 0)?.Root?["m_Transitions"]?.Seq);
            foreach (var child in machine?["m_ChildStateMachines"]?.Seq ?? new())
                branches.AddRange(Machine(child["m_StateMachine"]?.FileID ?? 0, path));
            path.Remove(fileId);
            return branches.OrderBy(b => b.Conditions.Count == 0 ? -1 : b.Conditions[0].Parameter == "GestureLeft" ? 0 : 1)
                .ThenBy(b => b.Conditions.FirstOrDefault()?.Threshold ?? -1).ToList();

            void Transitions(IEnumerable<YamlNode> refs)
            {
                foreach (var reference in refs ?? Enumerable.Empty<YamlNode>())
                {
                    var transition = scene.Doc(reference.FileID ?? 0)?.Root;
                    if (transition == null || transition["m_Mute"]?.AsBool() == true) continue;
                    var source = transition["m_Conditions"]?.Seq ?? new();
                    var conditions = source.Where(c => c["m_ConditionEvent"]?.AsString() is "GestureLeft" or "GestureRight" && c["m_ConditionMode"]?.AsInt() == 6)
                        .Select(c => new ExpressionCondition(c["m_ConditionEvent"].AsString(), 6, c["m_EventTreshold"]?.AsFloat() ?? 0)).ToList();
                    if (conditions.Any(c => !float.IsFinite(c.Threshold) || c.Threshold != MathF.Truncate(c.Threshold) || c.Threshold is < 0 or > 7)) continue;
                    if (conditions.Count == 1 && conditions[0].Threshold == 0)
                        conditions.Add(new(conditions[0].Parameter == "GestureLeft" ? "GestureRight" : "GestureLeft", 6, 0));
                    if (conditions.Count == 0 && (source.Count > 0 && source.All(c => c["m_ConditionMode"]?.AsInt() is 1 or 2) ||
                        source.Any(c => _contacts.Contains(c["m_ConditionEvent"]?.AsString() ?? "") || _physics.Contains(c["m_ConditionEvent"]?.AsString() ?? "")))) continue;
                    var branch = State(scene, transition["m_DstState"]?.FileID ?? 0, conditions);
                    if (branch != null) branches.Add(branch);
                }
            }
        }
    }

    private FaceEmoBranch State(UnityScene scene, long id, List<ExpressionCondition> conditions)
    {
        if (scene.Doc(id) is not { ClassId: 1102 } state) return null;
        if (conditions.Count == 0 && state.Root["m_TimeParameterActive"]?.AsBool() == true &&
            _physics.Contains(state.Root["m_TimeParameter"]?.AsString() ?? "")) return null;
        var motion = state.Root["m_Motion"];
        var candidates = new List<ExpressionClip>();
        // IsFaceMotion checks the clip, or only immediate first/last clips of a BlendTree.
        if (motion?.Guid != null) AddFace(motion);
        else if (scene.Doc(motion?.FileID ?? 0) is { ClassId: 206 } tree)
        {
            var children = tree.Root["m_Childs"]?.Seq;
            AddFace(children?.FirstOrDefault()?["m_Motion"]); AddFace(children?.LastOrDefault()?["m_Motion"]);
        }
        if (candidates.Count == 0) return null;
        var last = EndMotion(motion, false, new());
        bool grip = state.Root["m_TimeParameterActive"]?.AsBool() == true && conditions.Any(c => c.Threshold == 1);
        var first = grip ? EndMotion(motion, true, new()) : last;
        return new() { Name = state.Root["m_Name"]?.AsString() ?? last?.Name ?? first?.Name ?? "Expression",
            Motion = last, BaseMotion = first, Grip = grip, BaseAtEnd = motion?.Guid == null,
            Conditions = conditions, FaceMotions = candidates };

        void AddFace(YamlNode reference)
        {
            var clip = reference?.Guid == null ? null : _readClip(reference);
            if (clip?.Curves.Any(c => !VrchatExpressionDetection.IsViseme(c.Binding.Shape)) == true) candidates.Add(clip);
        }
        ExpressionClip EndMotion(YamlNode reference, bool first, HashSet<long> path)
        {
            if (reference?.Guid != null) return _readClip(reference);
            long fileId = reference?.FileID ?? 0;
            if (fileId == 0 || !path.Add(fileId) || scene.Doc(fileId) is not { ClassId: 206 } tree) return null;
            var children = tree.Root["m_Childs"]?.Seq;
            return EndMotion((first ? children?.FirstOrDefault() : children?.LastOrDefault())?["m_Motion"], false, path);
        }
    }

    public static void SelectFirstSet(ExpressionModel model, IReadOnlyDictionary<ExpressionBinding, float> baseline = null)
    {
        var imported = model.ImportedPatterns;
        bool HasFace(FaceEmoBranch branch) => branch.FaceMotions.Any(c => c.Curves.Count > 0);
        bool Difference(ExpressionClip clip, int endpoint = 0) => clip?.Curves.Any(c => baseline == null ||
            (endpoint == 0 ? c.Keys.Any(k => !VrchatExpressionDetection.Approximately(k.Value, baseline[c.Binding])) :
                !VrchatExpressionDetection.Approximately((endpoint < 0 ? c.Keys[0] : c.Keys[^1]).Value, baseline[c.Binding]))) == true;
        bool HasAnimation(FaceEmoBranch b) => Difference(b.Motion, b.Grip ? 1 : 0) || b.Grip && Difference(b.BaseMotion, b.BaseAtEnd ? 1 : -1);
        List<FaceEmoBranch> selected;
        string name = "FaceEmo expression set 1";
        if (imported.Cac)
        {
            var pattern = imported.Patterns.FirstOrDefault(p => p.Branches.Any(HasFace));
            selected = pattern?.Branches.Where(HasFace).ToList() ?? new();
            name = pattern?.Name ?? name;
        }
        else
        {
            var branches = imported.Patterns.AsEnumerable().Reverse().SelectMany(p => p.Branches).Where(b => HasFace(b) && HasAnimation(b)).ToArray();
            var conditional = branches.Where(b => b.Conditions.Count > 0).ToList();
            // DivideMode moves fully shadowed conditional branches to later patterns. Only retain pattern 1.
            var covered = new HashSet<int>(); selected = new();
            foreach (var branch in conditional)
            {
                var pairs = Enumerable.Range(0, 64).Where(i => branch.Conditions.All(c =>
                    (c.Parameter == "GestureLeft" ? i / 8 : i % 8) == c.Threshold)).ToArray();
                if (pairs.Any(i => !covered.Contains(i))) selected.Add(branch);
                covered.UnionWith(pairs);
            }
            // FaceEmo retains unconditioned animations as manual candidates, never as a catch-all gesture.
            // Deduplicate BEFORE DivideMode: even a branch moved to a later pattern suppresses its duplicate.
            var identities = conditional.SelectMany(AnimationIdentities).ToHashSet(StringComparer.Ordinal);
            foreach (var branch in branches.Where(b => b.Conditions.Count == 0))
                if (branch.Motion != null && identities.Add(branch.Motion.Id)) selected.Add(branch);
        }
        model.Layers.Clear(); model.Menu.Clear(); model.DetectedExpressions.Clear();
        // A fist can have a face at its base endpoint and no face at its full-grip endpoint.
        // Keep an explicit baseline pose so that branch still wins instead of falling through.
        var motions = selected.ToDictionary(b => b, b => b.Motion?.Curves.Count > 0 ? b.Motion : BaselineMotion(b));
        var retained = motions.Values.DistinctBy(c => c.Id).ToList();
        if (selected.Count > 0)
        {
            var layer = new ExpressionLayer { Id = "face-emo:first", Name = name, DefaultState = 0, EmptyStatesUseBaseStream = true };
            layer.States.Add(new("Neutral (authored baseline)", null, 1, true));
            foreach (var branch in selected.Where(b => b.Conditions.Count > 0))
            {
                var transition = new ExpressionTransition { Destination = layer.States.Count, CanTransitionToSelf = true };
                transition.Conditions.AddRange(branch.Conditions); layer.Transitions.Add(transition);
                string time = branch.Grip ? branch.Conditions.First(c => c.Threshold == 1).Parameter + "Weight" : null;
                layer.States.Add(new(branch.Name, motions[branch].Id, 1, true, time));
            }
            layer.Transitions.Add(new() { Destination = 0, CanTransitionToSelf = true }); model.Layers.Add(layer);
            model.DetectedExpressions.AddRange(retained);
        }
        if (baseline != null)
        {
            model.Clips.Clear(); model.Clips.AddRange(retained);
            string message = $"FaceEmo {(imported.Cac ? "CAC" : "normal")}: selected first set ({name}), " +
                $"{selected.Count(b => b.Conditions.Count > 0)} gesture branches, {selected.Count(b => b.Conditions.Count == 0)} manual candidates. " +
                "Later patterns, Animator routing, parameter defaults/drivers and Expression Menu are not imported.";
            model.Diagnostics.Add(message); UniLog.Log(message);
        }

        ExpressionClip BaselineMotion(FaceEmoBranch branch)
        {
            var pose = new ExpressionClip { Id = "face-emo:baseline:" + branch.FaceMotions[0].Id,
                Name = branch.Name, Source = "FaceEmo empty endpoint", Duration = 1 };
            foreach (var binding in branch.FaceMotions.SelectMany(c => c.Curves).Select(c => c.Binding).Distinct())
            {
                var curve = new ExpressionCurve { Binding = binding };
                curve.Keys.Add(new(0, baseline?.GetValueOrDefault(binding) ?? 0, 0, 0)); pose.Curves.Add(curve);
            }
            return pose;
        }
        IEnumerable<string> AnimationIdentities(FaceEmoBranch b)
        {
            if (!b.Grip) { if (Difference(b.Motion)) yield return b.Motion.Id; }
            else
            {
                if (Difference(b.BaseMotion, b.BaseAtEnd ? 1 : -1)) yield return b.BaseMotion.Id + (b.BaseAtEnd ? ":last" : ":first");
                if (Difference(b.Motion, 1)) yield return b.Motion.Id + ":last";
            }
        }
    }
}