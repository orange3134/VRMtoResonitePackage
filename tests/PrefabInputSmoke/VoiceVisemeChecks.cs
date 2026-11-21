using System.Text;
using VrmToResonitePackage.Unity;
using VrmToResonitePackage.Vrchat;

internal static class VoiceVisemeChecks
{
    public static void Run()
    {
        // Independent fixture. Entry 0 is the last, unconditional silence fallback.
        var text = new StringBuilder("--- !u!1107 &1\nAnimatorStateMachine:\n  m_EntryTransitions:\n");
        foreach (int i in Enumerable.Range(1, 14).Append(0)) text.AppendLine($"  - {{fileID: {100 + i}}}");
        var clips = new Dictionary<long, YamlNode>();
        for (int i = 0; i < 15; i++)
        {
            text.AppendLine($"--- !u!1109 &{100 + i}\nAnimatorTransition:\n  m_DstState: {{fileID: {200 + i}}}");
            text.AppendLine(i == 0 ? "  m_Conditions: []" :
                $"  m_Conditions:\n  - m_ConditionEvent: Viseme\n    m_ConditionMode: 6\n    m_EventTreshold: {i}");
            text.AppendLine($"--- !u!1102 &{200 + i}\nAnimatorState:\n  m_TimeParameterActive: 1\n  m_TimeParameter: Voice\n  m_Motion: {{fileID: {400 + i}}}\n  m_Transitions:\n  - {{fileID: {300 + i}}}");
            text.AppendLine($"--- !u!1101 &{300 + i}\nAnimatorStateTransition:\n  m_IsExit: 1\n  m_Conditions:\n  - m_ConditionEvent: Viseme\n    m_ConditionMode: 7\n    m_EventTreshold: {i}");
            clips.Add(400 + i, Clip(i));
        }
        var layer = UnityYaml.ParseFlatDocument("m_StateMachine: {fileID: 1}");
        var controller = UnityScene.Parse(text.ToString());
        (string Path, string Name)[] Read() => VrchatVoiceVisemes.Read(controller, layer, m => clips[m.FileID.Value]);
        var channels = Read();
        if (channels == null || !channels.SequenceEqual(Enumerable.Range(0, 15).Select(i => ("Face", "phoneme" + i))))
            throw new Exception("Voice-driven phonemes and neutral resets did not resolve");
        clips[404] = Clip(4, "accessory");
        if (Read() != null) throw new Exception("A reset outside the viseme bank must not be discarded");
        clips[404] = Clip(4);
        controller = UnityScene.Parse(text.ToString().Replace("m_TimeParameter: Voice", "m_TimeParameter: Costume"));
        if (Read() != null) throw new Exception("An unrelated time parameter must not drive the mouth");
        // Fourteen explicit routes and the default state select all fifteen poses.
        string routed = text.ToString().Replace("- {fileID: 100}", "m_DefaultState: {fileID: 200}")
            .Replace("m_TimeParameterActive: 1", "m_TimeParameterActive: 0");
        controller = UnityScene.Parse(routed);
        foreach (int i in Enumerable.Range(0, 15)) clips[400 + i] = Clip(i, timed: true);
        if (Read()?.SequenceEqual(channels) != true) throw new Exception("Default silence and non-looping mouth poses");
        clips[403] = Clip(3);
        if (Read() != null) throw new Exception("Missing non-looping playback settings");

        // A selectable submachine has only five nonzero poses. The declaration
        // still lists fifteen shapes; it must never create the other ten bindings.
        string nested = "--- !u!1107 &2\nAnimatorStateMachine:\n  m_EntryTransitions:\n  - {fileID: 90}\n" +
            "--- !u!1109 &90\nAnimatorTransition:\n  m_DstStateMachine: {fileID: 1}\n  m_Conditions:\n" +
            "  - m_ConditionMode: 6\n    m_ConditionEvent: SpeakerMode\n    m_EventTreshold: 2\n";
        controller = UnityScene.Parse(routed + nested);
        layer = UnityYaml.ParseFlatDocument("m_StateMachine: {fileID: 2}");
        var declared = channels.Select(c => c.Name).ToArray();
        var reachable = new HashSet<long> { 1 };
        for (int i = 0; i < 15; i++) clips[400 + i] = Clip(i < 10 ? -1 : i, timed: true);
        var partial = VrchatVoiceVisemes.Read(controller, layer, m => clips[m.FileID.Value], "Face", declared, reachable);
        if (partial == null || partial.Take(10).Any(c => c != default) ||
            !partial.Skip(10).SequenceEqual(channels.Skip(10))) throw new Exception("Authored vowel subset");
        reachable.Clear();
        if (VrchatVoiceVisemes.Read(controller, layer, m => clips[m.FileID.Value], "Face", declared, reachable) != null)
            throw new Exception("Unreachable phoneme bank accepted");
        Console.WriteLine("PASS: Voice-driven viseme bank, reset ownership and time parameter");

        static YamlNode Clip(int active, string differentReset = null, bool timed = false)
        {
            var yaml = new StringBuilder("m_FloatCurves:\n");
            for (int j = 0; j < 15; j++)
            {
                string name = j == 0 && differentReset != null ? differentReset : "phoneme" + j;
                yaml.AppendLine($"- classID: 137\n  path: Face\n  attribute: blendShape.{name}\n  curve:\n    m_Curve:");
                yaml.AppendLine($"    - value: {(j == active && active == 0 ? 100 : 0)}");
                yaml.AppendLine($"    - value: {(j == active ? 100 : 0)}");
            }
            if (timed) yaml.AppendLine("m_AnimationClipSettings:\n  m_LoopTime: 0");
            return UnityYaml.ParseFlatDocument(yaml.ToString());
        }
    }
}
