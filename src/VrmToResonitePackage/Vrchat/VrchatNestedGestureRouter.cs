using System.Globalization;
using VrmToResonitePackage.Expressions;
using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Selects nested expression banks from authored defaults before projecting their hand routes.</summary>
internal static class VrchatNestedGestureRouter
{
    internal static bool TryProject(UnityPackage package, UnityScene scene, YamlNode machine, YamlNode mask,
        ExpressionLayer original, IReadOnlyDictionary<string, ExpressionParameter> parameters,
        Func<YamlNode, ExpressionClip> readClip, out ExpressionLayer result, out string detail)
    {
        result = null; detail = null;
        var visited = new HashSet<YamlNode>();
        var defaults = new Dictionary<string, float>(StringComparer.Ordinal);
        bool failed = false;
        while ((machine?["m_ChildStateMachines"]?.Seq?.Count ?? 0) > 0)
        {
            if (!visited.Add(machine) || (machine["m_StateMachineBehaviours"]?.Seq?.Count ?? 0) != 0 ||
                VrchatAnimatorDefaults.ActiveTransitions(scene, machine["m_AnyStateTransitions"]?.Seq).Any()) return false;
            bool Matches(YamlNode transition)
            {
                if (transition == null) { failed = true; return false; }
                bool match = true;
                foreach (var condition in transition["m_Conditions"]?.Seq ?? new())
                {
                    string name = condition["m_ConditionEvent"]?.AsString();
                    int mode = condition["m_ConditionMode"]?.AsInt() ?? 0;
                    float threshold = condition["m_EventTreshold"]?.AsFloat(float.NaN) ?? 0;
                    if (name == null || name is "GestureLeft" or "GestureRight" or "GestureLeftWeight" or "GestureRightWeight" ||
                        mode is not (1 or 2 or 3 or 4 or 6 or 7) || !float.IsFinite(threshold) ||
                        !parameters.TryGetValue(name, out var parameter) || parameter.Type is not (1 or 3 or 4) || !float.IsFinite(parameter.Default))
                    { failed = true; return false; }
                    defaults[name] = parameter.Default;
                    match &= new ExpressionCondition(name, mode, threshold).Matches(parameter.Default);
                }
                return match;
            }
            var entry = VrchatAnimatorDefaults.ActiveTransitions(scene, machine["m_EntryTransitions"]?.Seq)
                .Select(r => scene.Doc(r.FileID ?? 0)?.Root).FirstOrDefault(Matches);
            if (failed || entry == null || entry["m_IsExit"]?.AsBool() == true || entry["m_HasExitTime"]?.AsBool() == true ||
                (entry["m_DstState"]?.FileID ?? 0) != 0 || (entry["m_TransitionOffset"]?.AsFloat() ?? 0) != 0 ||
                (entry["m_InterruptionSource"]?.AsInt() ?? 0) != 0) return false;
            long child = entry["m_DstStateMachine"]?.FileID ?? 0;
            if (child == 0 || !machine["m_ChildStateMachines"].Seq.Any(c => c["m_StateMachine"]?.FileID == child) ||
                scene.Doc(child) is not { ClassId: 1107 } document) return false;
            machine = document.Root;
        }
        if (visited.Count == 0 || machine == null || !visited.Add(machine)) return false;
        // Do not silently discard a driver that changes this bank's selection predicates.
        var conditions = defaults.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var owner in new[] { machine }.Concat((machine["m_ChildStates"]?.Seq ?? new())
                     .Select(c => scene.Doc(c["m_State"]?.FileID ?? 0)?.Root)))
            foreach (string key in new[] { "m_EntryTransitions", "m_AnyStateTransitions", "m_Transitions" })
                foreach (var r in VrchatAnimatorDefaults.ActiveTransitions(scene, owner?[key]?.Seq))
                    foreach (var c in scene.Doc(r.FileID ?? 0)?.Root?["m_Conditions"]?.Seq ?? new())
                        if (c["m_ConditionEvent"]?.AsString() is string name) conditions.Add(name);
        foreach (var child in machine["m_ChildStates"]?.Seq ?? new())
            foreach (var r in scene.Doc(child["m_State"]?.FileID ?? 0)?.Root?["m_StateMachineBehaviours"]?.Seq ?? new())
                if (scene.Doc(r.FileID ?? 0)?.Root?["parameters"]?.Seq?.Any(w => conditions.Contains(w["name"]?.AsString() ?? "")) == true)
                    return false;
        if (!VrchatDefaultGestureRouter.TryProject(package, scene, machine, mask, original, parameters, readClip,
                out result, out string inner, includeHandOnly: true, allowEmptyTimedFallback: true)) return false;
        detail = string.Join(", ", defaults.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture))) + "; " + inner;
        return true;
    }
}