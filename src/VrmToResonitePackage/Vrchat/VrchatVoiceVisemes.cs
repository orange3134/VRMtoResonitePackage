using VrmToResonitePackage.Unity;

namespace VrmToResonitePackage.Vrchat;

/// <summary>Recognizes FX entry routing for a complete set of Voice-scrubbed viseme clips.</summary>
internal static class VrchatVoiceVisemes
{
    internal static (string Path, string Name)[] Read(UnityScene controller, YamlNode layer,
        Func<YamlNode, YamlNode> readClip)
    {
        if ((layer["m_Mask"]?.FileID ?? 0) != 0 || (layer["m_SyncedLayerIndex"]?.AsInt() ?? -1) >= 0) return null;
        var machine = controller.Doc(layer["m_StateMachine"]?.FileID ?? 0)?.Root;
        var entries = Transitions(machine, "m_EntryTransitions");
        if (entries.Length != 15) return null;
        var channels = new (string Path, string Name)[15];
        var poses = new HashSet<(string Path, string Name)>[15];
        YamlNode silence = null;
        for (int i = 0; i < entries.Length; i++)
        {
            var conditions = entries[i]["m_Conditions"]?.Seq;
            int phoneme;
            if (conditions?.Count == 0 && i == 14) phoneme = 0;
            else if (conditions?.Count == 1 && conditions[0]["m_ConditionEvent"]?.AsString() == "Viseme" &&
                     conditions[0]["m_ConditionMode"]?.AsInt() == 6 &&
                     conditions[0]["m_EventTreshold"]?.AsFloat() is float v && v >= 0 && v < 15 && v == (int)v)
                phoneme = (int)v;
            else return null;
            if (poses[phoneme] != null) return null;
            var state = Destination(entries[i]);
            if (state == null || (phoneme != 0 && (state["m_TimeParameterActive"]?.AsBool() != true ||
                state["m_TimeParameter"]?.AsString() != "Voice"))) return null;
            // Each state returns to entry selection exactly when the phoneme changes.
            var exits = Transitions(state, "m_Transitions");
            if (exits.Length != 1 || exits[0]["m_IsExit"]?.AsBool() != true ||
                exits[0]["m_Conditions"]?.Seq is not { Count: 1 } exit ||
                exit[0]["m_ConditionEvent"]?.AsString() != "Viseme" || Enumerable.Range(0, 15).Any(v =>
                    VrchatAnimatorGraph.Matches(exit[0], v) != (v != phoneme))) return null;
            if (phoneme == 0) silence = state;
            poses[phoneme] = ReadPose(readClip(state["m_Motion"]), phoneme == 0, out var active);
            if (poses[phoneme] == null || active == default) return null;
            channels[phoneme] = active;
        }
        // Zero curves may be discarded only when the other fourteen drivers own precisely
        // those fields. Never silently discard a reset of an expression or accessory.
        if (channels.Distinct().Count() != 15 || poses.Any(p => !p.SetEquals(channels))) return null;

        var interrupts = Transitions(machine, "m_AnyStateTransitions");
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
        var cleared = ReadPose(readClip(off?["m_Motion"]), false, out var activeOff);
        return activeOff == default && cleared?.SetEquals(channels) == true ? channels : null;

        YamlNode[] Transitions(YamlNode node, string field) =>
            VrchatAnimatorDefaults.ActiveTransitions(controller, node?[field]?.Seq)
                .Select(r => controller.Doc(r.FileID.Value).Root).ToArray();
        YamlNode Destination(YamlNode t) => controller.Doc(t["m_DstState"]?.FileID ?? 0)?.Root;
    }

    private static HashSet<(string, string)> ReadPose(YamlNode clip, bool silence, out (string, string) active)
    {
        active = default;
        if (clip?["m_FloatCurves"]?.Seq is not { Count: 15 } curves || (clip["m_PPtrCurves"]?.Seq?.Count ?? 0) != 0)
            return null;
        var channels = new HashSet<(string, string)>();
        foreach (var curve in curves)
        {
            string attribute = curve["attribute"]?.AsString();
            if (curve["classID"]?.AsInt() != 137 || attribute?.StartsWith("blendShape.", StringComparison.Ordinal) != true ||
                curve["path"]?.AsString() is not string path || curve["curve"]?["m_Curve"]?.Seq is not { Count: > 0 } keys)
                return null;
            var channel = (path, attribute["blendShape.".Length..]);
            if (!channels.Add(channel)) return null;
            var values = keys.Select(k => k["value"]?.AsFloat(float.NaN) ?? float.NaN).ToArray();
            if (values.All(v => v == 0) && keys.All(k =>
                (k["inSlope"]?.AsFloat() ?? 0) == 0 && (k["outSlope"]?.AsFloat() ?? 0) == 0)) continue;
            if (active != default || values[0] != (silence ? 100 : 0) || values[^1] != 100 ||
                values.Any(v => !float.IsFinite(v) || v < 0 || v > 100) ||
                !values.SequenceEqual(values.Order())) return null;
            active = channel;
        }
        return channels;
    }
}
