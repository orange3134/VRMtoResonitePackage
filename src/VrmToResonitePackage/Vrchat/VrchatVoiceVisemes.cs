using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Reads phoneme poses from FX entry transitions, including selectable voice banks.</summary>
internal static class VrchatVoiceVisemes
{
    internal static (string Path, string Name)[] Read(UnityScene controller, YamlNode layer,
        Func<YamlNode, YamlNode> readClip, string faceName = null, string[] declared = null,
        IReadOnlySet<long> reachable = null)
    {
        if ((layer["m_Mask"]?.FileID ?? 0) != 0 || (layer["m_SyncedLayerIndex"]?.AsInt() ?? -1) >= 0) return null;
        var machine = controller.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root;
        var direct = ReadBank(controller, machine, readClip);
        if (direct != null || faceName == null || declared?.Length != 15 ||
            declared.Any(string.IsNullOrEmpty) || declared.Distinct().Count() != 15) return direct;

        // Some controllers select a speaking character with an integer mode.
        // Project reachable voice banks onto the descriptor's face. Require actual
        // animation curves to confirm every emitted shape; all-zero poses stay unbound.
        (string Path, string Name)[] selected = null;
        foreach (var route in VrchatAnimatorDefaults.ActiveTransitions(controller, machine?["m_EntryTransitions"]?.Seq))
        {
            var entry = controller.Doc(route.FileID.Value).Root;
            long id = entry["m_DstStateMachine"]?.FileID ?? 0;
            if (id == 0 || reachable?.Contains(id) != true || entry["m_Conditions"]?.Seq is not { Count: 1 } mode ||
                mode[0]["m_ConditionMode"]?.AsInt() != 6 || mode[0]["m_ConditionEvent"]?.AsString() is null or "Viseme")
                continue;
            var candidate = ReadBank(controller, controller.Doc(id)?.Root, readClip, faceName, declared);
            if (candidate == null) continue;
            if (selected != null && !selected.SequenceEqual(candidate)) return null;
            selected = candidate;
        }
        return selected;
    }

    private static (string Path, string Name)[] ReadBank(UnityScene controller, YamlNode bank,
        Func<YamlNode, YamlNode> readClip, string projectedFace = null, string[] declared = null)
    {
        var entries = Transitions(bank, "m_EntryTransitions");
        if (entries.Length is not (14 or 15)) return null;
        var channels = new (string Path, string Name)[15];
        var poses = new HashSet<(string Path, string Name)>[15];
        YamlNode silence = null;
        for (int i = 0; i < 15; i++)
        {
            bool useDefault = i == entries.Length;
            var conditions = useDefault ? null : entries[i]["m_Conditions"]?.Seq;
            int phoneme;
            if (useDefault || conditions?.Count == 0 && i == 14) phoneme = 0;
            else if (conditions?.Count == 1 && conditions[0]["m_ConditionEvent"]?.AsString() == "Viseme" &&
                     conditions[0]["m_ConditionMode"]?.AsInt() == 6 &&
                     conditions[0]["m_EventTreshold"]?.AsFloat() is float v && v >= 0 && v < 15 && v == (int)v)
                phoneme = (int)v;
            else return null;
            if (poses[phoneme] != null) return null;
            var state = useDefault ? controller.Doc(bank["m_DefaultState"]?.FileID ?? 0)?.Root : Destination(entries[i]);
            if (state == null) return null;
            bool scrubbed = state["m_TimeParameterActive"]?.AsBool() == true;
            if (scrubbed && state["m_TimeParameter"]?.AsString() != "Voice") return null;
            var clip = readClip(state["m_Motion"]);
            if (!scrubbed && phoneme != 0 && (clip?["m_AnimationClipSettings"]?["m_LoopTime"]?.AsBool() != false ||
                (state["m_Speed"]?.AsFloat() ?? 1) <= 0 || state["m_SpeedParameterActive"]?.AsBool() == true)) return null;
            // Each state returns to entry selection exactly when the phoneme changes.
            var exits = Transitions(state, "m_Transitions");
            if (exits.Length != 1 || exits[0]["m_IsExit"]?.AsBool() != true ||
                exits[0]["m_Conditions"]?.Seq is not { Count: 1 } exit ||
                exit[0]["m_ConditionEvent"]?.AsString() != "Viseme" || Enumerable.Range(0, 15).Any(v =>
                    VrchatAnimatorGraph.Matches(exit[0], v) != (v != phoneme))) return null;
            if (phoneme == 0) silence = state;
            poses[phoneme] = ReadPose(clip, phoneme == 0, out var active, projectedFace);
            if (poses[phoneme] == null || (projectedFace == null ? active == default :
                active != default && active.Name != declared[phoneme])) return null;
            channels[phoneme] = active;
        }
        // Every pose must reset the same mouth bank. In a selected mode the
        // descriptor identifies that bank even when some phonemes stay neutral.
        if (projectedFace != null)
        {
            var path = channels.FirstOrDefault(c => c != default).Path;
            if (path == null || poses.Any(p => !p.SetEquals(declared.Select(n => (path, n))))) return null;
        }
        else if (channels.Distinct().Count() != 15 || poses.Any(p => !p.SetEquals(channels))) return null;

        var interrupts = Transitions(bank, "m_AnyStateTransitions");
        if (interrupts.Length == 0) return channels;
        // An optional mouth-cancel switch selects a pose that clears the whole bank.
        // Import its enabled phoneme assignments for native voice tracking.
        if (interrupts.Length != 1) return null;
        var gate = interrupts[0]["m_Conditions"]?.Seq;
        if (gate?.Count != 1 || gate[0]["m_ConditionMode"]?.AsInt() is not (1 or 2)) return null;
        var off = Destination(interrupts[0]);
        var resume = Transitions(off, "m_Transitions");
        if (resume.Length != 1 || Destination(resume[0]) != silence ||
            resume[0]["m_Conditions"]?.Seq is not { Count: 1 } on ||
            on[0]["m_ConditionEvent"]?.AsString() != gate[0]["m_ConditionEvent"]?.AsString() ||
            on[0]["m_ConditionMode"]?.AsInt() != 3 - gate[0]["m_ConditionMode"].AsInt()) return null;
        var offClip = readClip(off?["m_Motion"]);
        if (offClip?["m_FloatCurves"]?.Seq is { } offCurves && offCurves.All(c =>
                c["attribute"]?.AsString()?.StartsWith("blendShape.", StringComparison.Ordinal) == false)) return channels;
        var cleared = ReadPose(offClip, false, out var activeOff);
        return activeOff == default && cleared?.SetEquals(channels) == true ? channels : null;

        YamlNode[] Transitions(YamlNode node, string field) =>
            VrchatAnimatorDefaults.ActiveTransitions(controller, node?[field]?.Seq)
                .Select(r => controller.Doc(r.FileID.Value).Root).ToArray();
        YamlNode Destination(YamlNode t) => controller.Doc(t["m_DstState"]?.FileID ?? 0)?.Root;
    }

    private static HashSet<(string, string)> ReadPose(YamlNode clip, bool silence, out (string Path, string Name) active,
        string projectedFace = null)
    {
        active = default;
        if (clip?["m_FloatCurves"]?.Seq is not { } curves || (clip["m_PPtrCurves"]?.Seq?.Count ?? 0) != 0)
            return null;
        var channels = new HashSet<(string, string)>();
        foreach (var curve in curves)
        {
            string attribute = curve["attribute"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true ||
                curve["path"]?.AsString() is not string path || curve["curve"]?["m_Curve"]?.Seq is not { Count: > 0 } keys)
                return null;
            if (projectedFace != null && path.Split('/').Last() != projectedFace) continue;
            var channel = (path, attribute["blendShape.".Length..]);
            if (!channels.Add(channel)) return null;
            var values = keys.Select(k => k["value"]?.AsFloat(float.NaN) ?? float.NaN).ToArray();
            if (values.All(v => v == 0) && keys.All(k =>
                (k["inSlope"]?.AsFloat() ?? 0) == 0 && (k["outSlope"]?.AsFloat() ?? 0) == 0)) continue;
            if (active != default || (values[0] != (silence ? 100 : 0) && !values.All(v => v == 100)) || values[^1] != 100 ||
                values.Any(v => !float.IsFinite(v) || v < 0 || v > 100) ||
                !values.SequenceEqual(values.Order())) return null;
            active = channel;
        }
        return channels.Count == 15 ? channels : null;
    }
}
