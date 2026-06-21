using System.Globalization;
using System.Text;

namespace VrmToResonitePackage.Unity;

/// <summary>
/// A single parsed Unity-YAML node: either a scalar, a mapping or a sequence.
/// Unity asset files are a restricted, well-behaved YAML subset (2-space block
/// indentation, inline flow maps/sequences, no anchors/aliases inside documents),
/// so this lightweight parser is sufficient and avoids a third-party dependency.
/// </summary>
public sealed class YamlNode
{
    public string ScalarValue { get; init; }
    public Dictionary<string, YamlNode> Map { get; init; }
    public List<YamlNode> Seq { get; init; }

    public static readonly YamlNode Empty = new() { ScalarValue = "" };

    public bool IsScalar => ScalarValue != null;
    public bool IsMap => Map != null;
    public bool IsSeq => Seq != null;

    public YamlNode this[string key] => Map != null && Map.TryGetValue(key, out YamlNode n) ? n : null;

    public IReadOnlyList<YamlNode> Items => Seq ?? (IReadOnlyList<YamlNode>)Array.Empty<YamlNode>();

    public string AsString() => ScalarValue;

    public float AsFloat(float fallback = 0f)
        => ScalarValue != null && float.TryParse(ScalarValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v : fallback;

    public int AsInt(int fallback = 0)
    {
        if (ScalarValue == null) return fallback;
        if (int.TryParse(ScalarValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        // Some fields are stored as floats ("1") but read as ints elsewhere.
        if (float.TryParse(ScalarValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return (int)f;
        return fallback;
    }

    public long AsLong(long fallback = 0)
        => ScalarValue != null && long.TryParse(ScalarValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
            ? v : fallback;

    public bool AsBool(bool fallback = false)
    {
        if (ScalarValue == null) return fallback;
        return ScalarValue == "1" || string.Equals(ScalarValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fileID of a Unity reference flow map (<c>{fileID: N, ...}</c>), or null.</summary>
    public long? FileID
    {
        get
        {
            YamlNode f = this["fileID"];
            return f != null && f.ScalarValue != null &&
                   long.TryParse(f.ScalarValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)
                ? v : null;
        }
    }

    /// <summary>The guid of a Unity asset reference flow map, or null.</summary>
    public string Guid => this["guid"]?.ScalarValue;

    public float Vec(string axis, float fallback = 0f) => this[axis]?.AsFloat(fallback) ?? fallback;
}

/// <summary>One Unity-YAML document: <c>--- !u!&lt;classId&gt; &amp;&lt;fileId&gt;[ stripped]</c> + body.</summary>
public sealed class YamlDocument
{
    public int ClassId { get; init; }
    public long FileId { get; init; }
    public bool Stripped { get; init; }

    /// <summary>The top-level type name of the document (e.g. "GameObject", "Transform", "MonoBehaviour").</summary>
    public string TypeName { get; init; }

    /// <summary>The document body (the fields under the type name).</summary>
    public YamlNode Root { get; init; }
}

public static class UnityYaml
{
    /// <summary>Parses a multi-document Unity asset file (prefab/scene/.mat/.asset).</summary>
    public static List<YamlDocument> ParseDocuments(string text)
    {
        var documents = new List<YamlDocument>();
        string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        int i = 0;
        // Skip the directives header (%YAML / %TAG) until the first document marker.
        while (i < rawLines.Length && !rawLines[i].StartsWith("--- ", StringComparison.Ordinal))
        {
            i++;
        }

        while (i < rawLines.Length)
        {
            string header = rawLines[i];
            i++;
            (int classId, long fileId, bool stripped) = ParseHeader(header);

            var body = new List<string>();
            while (i < rawLines.Length && !rawLines[i].StartsWith("--- ", StringComparison.Ordinal))
            {
                body.Add(rawLines[i]);
                i++;
            }

            (string typeName, YamlNode root) = ParseDocumentBody(body);
            documents.Add(new YamlDocument
            {
                ClassId = classId,
                FileId = fileId,
                Stripped = stripped,
                TypeName = typeName,
                Root = root,
            });
        }
        return documents;
    }

    /// <summary>
    /// Parses a single-document plain YAML file with no <c>--- !u!</c> markers (e.g. an asset ".meta"),
    /// returning its top-level mapping.
    /// </summary>
    public static YamlNode ParseFlatDocument(string text)
    {
        string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var body = new List<string>();
        foreach (string line in rawLines)
        {
            if (line.StartsWith("%", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                continue;
            }
            body.Add(line);
        }
        List<Line> lines = Tokenize(body);
        int idx = 0;
        return idx < lines.Count ? ParseBlock(lines, ref idx, lines[idx].Indent) : YamlNode.Empty;
    }

    private static (int classId, long fileId, bool stripped) ParseHeader(string header)
    {
        // "--- !u!1 &10517955995544831" or "--- !u!4 &123 stripped"
        int classId = 0;
        long fileId = 0;
        bool stripped = header.EndsWith(" stripped", StringComparison.Ordinal);

        int bang = header.IndexOf("!u!", StringComparison.Ordinal);
        if (bang >= 0)
        {
            int start = bang + 3;
            int end = start;
            while (end < header.Length && char.IsDigit(header[end])) end++;
            int.TryParse(header.AsSpan(start, end - start), out classId);
        }
        int amp = header.IndexOf('&');
        if (amp >= 0)
        {
            int start = amp + 1;
            int end = start;
            while (end < header.Length && char.IsDigit(header[end])) end++;
            long.TryParse(header.AsSpan(start, end - start), out fileId);
        }
        return (classId, fileId, stripped);
    }

    private static (string typeName, YamlNode root) ParseDocumentBody(List<string> rawBody)
    {
        List<Line> lines = Tokenize(rawBody);
        if (lines.Count == 0)
        {
            return (null, YamlNode.Empty);
        }
        // First line is "TypeName:" at indent 0; the rest (indent >= 2) is its mapping body.
        string first = lines[0].Text;
        string typeName = first.EndsWith(":", StringComparison.Ordinal) ? first[..^1] : first;

        int idx = 1;
        YamlNode root = idx < lines.Count
            ? ParseBlock(lines, ref idx, lines[idx].Indent)
            : YamlNode.Empty;
        return (typeName, root);
    }

    private readonly struct Line
    {
        public int Indent { get; init; }
        public string Text { get; init; } // trimmed of leading indentation, never empty
    }

    /// <summary>
    /// Splits the body into logical lines: blank lines dropped, and flow collections that
    /// Unity wraps across physical lines (long <c>{...}</c>) joined back into one line.
    /// </summary>
    private static List<Line> Tokenize(List<string> rawBody)
    {
        var lines = new List<Line>();
        for (int i = 0; i < rawBody.Count; i++)
        {
            string raw = rawBody[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;
            string content = raw[indent..];

            // Join wrapped flow collections (unbalanced { or [).
            while (HasUnbalancedFlow(content) && i + 1 < rawBody.Count)
            {
                i++;
                content += " " + rawBody[i].Trim();
            }

            // Unity wraps long plain scalars such as m_ShaderKeywords onto deeper-indented
            // continuation lines without a YAML block marker. If left as a separate token, the
            // mapping parser stops at that deeper line and silently drops every following field.
            int keyColon = FindKeyColon(content);
            if (keyColon >= 0 && content[(keyColon + 1)..].Trim().Length > 0)
            {
                while (i + 1 < rawBody.Count)
                {
                    string nextRaw = rawBody[i + 1];
                    if (string.IsNullOrWhiteSpace(nextRaw))
                    {
                        i++;
                        continue;
                    }
                    int nextIndent = 0;
                    while (nextIndent < nextRaw.Length && nextRaw[nextIndent] == ' ')
                    {
                        nextIndent++;
                    }
                    string nextContent = nextRaw[nextIndent..];
                    if (nextIndent <= indent || nextContent.StartsWith("- ", StringComparison.Ordinal) ||
                        FindKeyColon(nextContent) >= 0)
                    {
                        break;
                    }
                    i++;
                    content += " " + nextContent;
                }
            }

            lines.Add(new Line { Indent = indent, Text = content });
        }
        return lines;
    }

    private static bool HasUnbalancedFlow(string s)
    {
        int depth = 0;
        bool inQuote = false;
        char quote = '\0';
        foreach (char c in s)
        {
            if (inQuote)
            {
                if (c == quote) inQuote = false;
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    inQuote = true;
                    quote = c;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    break;
            }
        }
        return depth > 0;
    }

    private static YamlNode ParseBlock(List<Line> lines, ref int idx, int indent)
    {
        if (idx >= lines.Count)
        {
            return YamlNode.Empty;
        }
        return lines[idx].Text.StartsWith("- ", StringComparison.Ordinal) || lines[idx].Text == "-"
            ? ParseSequence(lines, ref idx, indent)
            : ParseMapping(lines, ref idx, indent);
    }

    private static YamlNode ParseMapping(List<Line> lines, ref int idx, int indent)
    {
        var map = new Dictionary<string, YamlNode>();
        while (idx < lines.Count && lines[idx].Indent == indent &&
               !lines[idx].Text.StartsWith("- ", StringComparison.Ordinal))
        {
            string text = lines[idx].Text;
            int colon = FindKeyColon(text);
            if (colon < 0)
            {
                idx++;
                continue;
            }
            string key = text[..colon].Trim();
            string rest = text[(colon + 1)..].Trim();
            idx++;

            if (rest.Length > 0)
            {
                map[key] = ParseScalarOrFlow(rest);
            }
            else if (idx < lines.Count && lines[idx].Indent > indent)
            {
                map[key] = ParseBlock(lines, ref idx, lines[idx].Indent);
            }
            else if (idx < lines.Count && lines[idx].Indent == indent &&
                     (lines[idx].Text.StartsWith("- ", StringComparison.Ordinal) || lines[idx].Text == "-"))
            {
                // Unity (YAML) places block sequence items at the SAME indent as their key.
                map[key] = ParseSequence(lines, ref idx, indent);
            }
            else
            {
                map[key] = YamlNode.Empty;
            }
        }
        return new YamlNode { Map = map };
    }

    private static YamlNode ParseSequence(List<Line> lines, ref int idx, int indent)
    {
        var seq = new List<YamlNode>();
        while (idx < lines.Count && lines[idx].Indent == indent &&
               (lines[idx].Text.StartsWith("- ", StringComparison.Ordinal) || lines[idx].Text == "-"))
        {
            string text = lines[idx].Text;
            string itemText = text.Length >= 2 ? text[2..] : "";

            // An item whose first content is "key: value" begins an inline mapping whose
            // remaining keys are indented two past the dash.
            int colon = FindKeyColon(itemText);
            if (colon >= 0)
            {
                // Rewrite this line as a mapping line at indent+2 and parse a mapping block.
                lines[idx] = new Line { Indent = indent + 2, Text = itemText };
                int itemIndent = indent + 2;
                seq.Add(ParseMapping(lines, ref idx, itemIndent));
            }
            else if (itemText.Length > 0)
            {
                seq.Add(ParseScalarOrFlow(itemText));
                idx++;
            }
            else
            {
                // "-" alone: nested block on following deeper lines.
                idx++;
                if (idx < lines.Count && lines[idx].Indent > indent)
                {
                    seq.Add(ParseBlock(lines, ref idx, lines[idx].Indent));
                }
                else
                {
                    seq.Add(YamlNode.Empty);
                }
            }
        }
        return new YamlNode { Seq = seq };
    }

    /// <summary>Finds the colon that separates a mapping key from its value, ignoring colons inside flow/quotes.</summary>
    private static int FindKeyColon(string text)
    {
        int depth = 0;
        bool inQuote = false;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuote)
            {
                if (c == quote) inQuote = false;
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    inQuote = true;
                    quote = c;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    break;
                case ':':
                    if (depth == 0 && (i + 1 == text.Length || text[i + 1] == ' '))
                    {
                        return i;
                    }
                    break;
            }
        }
        return -1;
    }

    private static YamlNode ParseScalarOrFlow(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return YamlNode.Empty;
        }
        if (value[0] == '{')
        {
            int pos = 0;
            return ParseFlowMap(value, ref pos);
        }
        if (value[0] == '[')
        {
            int pos = 0;
            return ParseFlowSeq(value, ref pos);
        }
        return new YamlNode { ScalarValue = Unquote(value) };
    }

    private static YamlNode ParseFlowMap(string s, ref int pos)
    {
        var map = new Dictionary<string, YamlNode>();
        pos++; // consume '{'
        SkipWs(s, ref pos);
        while (pos < s.Length && s[pos] != '}')
        {
            string key = ReadFlowToken(s, ref pos, stopAtColon: true).Trim();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ':') pos++;
            SkipWs(s, ref pos);
            YamlNode value = ReadFlowValue(s, ref pos);
            map[key] = value;
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; SkipWs(s, ref pos); }
        }
        if (pos < s.Length && s[pos] == '}') pos++;
        return new YamlNode { Map = map };
    }

    private static YamlNode ParseFlowSeq(string s, ref int pos)
    {
        var seq = new List<YamlNode>();
        pos++; // consume '['
        SkipWs(s, ref pos);
        while (pos < s.Length && s[pos] != ']')
        {
            seq.Add(ReadFlowValue(s, ref pos));
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ',') { pos++; SkipWs(s, ref pos); }
        }
        if (pos < s.Length && s[pos] == ']') pos++;
        return new YamlNode { Seq = seq };
    }

    private static YamlNode ReadFlowValue(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        if (pos >= s.Length)
        {
            return YamlNode.Empty;
        }
        if (s[pos] == '{') return ParseFlowMap(s, ref pos);
        if (s[pos] == '[') return ParseFlowSeq(s, ref pos);
        return new YamlNode { ScalarValue = Unquote(ReadFlowToken(s, ref pos, stopAtColon: false).Trim()) };
    }

    private static string ReadFlowToken(string s, ref int pos, bool stopAtColon)
    {
        var sb = new StringBuilder();
        bool inQuote = false;
        char quote = '\0';
        while (pos < s.Length)
        {
            char c = s[pos];
            if (inQuote)
            {
                if (quote == '"' && c == '\\')
                {
                    AppendDoubleQuotedEscape(sb, s, ref pos);
                    continue;
                }
                if (c == quote) { inQuote = false; pos++; continue; }
                sb.Append(c);
                pos++;
                continue;
            }
            if (c is '\'' or '"') { inQuote = true; quote = c; pos++; continue; }
            if (c == ',' || c == '}' || c == ']') break;
            if (stopAtColon && c == ':') break;
            sb.Append(c);
            pos++;
        }
        return sb.ToString();
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t')) pos++;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
        {
            return s[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            string value = s[1..^1];
            var result = new StringBuilder(value.Length);
            for (int pos = 0; pos < value.Length;)
            {
                if (value[pos] == '\\')
                {
                    AppendDoubleQuotedEscape(result, value, ref pos);
                }
                else
                {
                    result.Append(value[pos++]);
                }
            }
            return result.ToString();
        }
        return s;
    }

    private static void AppendDoubleQuotedEscape(StringBuilder result, string text, ref int pos)
    {
        pos++; // consume '\'
        if (pos >= text.Length)
        {
            result.Append('\\');
            return;
        }

        char escape = text[pos++];
        switch (escape)
        {
            case '0': result.Append('\0'); return;
            case 'a': result.Append('\a'); return;
            case 'b': result.Append('\b'); return;
            case 't': result.Append('\t'); return;
            case 'n': result.Append('\n'); return;
            case 'v': result.Append('\v'); return;
            case 'f': result.Append('\f'); return;
            case 'r': result.Append('\r'); return;
            case 'e': result.Append('\u001b'); return;
            case ' ': result.Append(' '); return;
            case '"': result.Append('"'); return;
            case '/': result.Append('/'); return;
            case '\\': result.Append('\\'); return;
            case 'N': result.Append('\u0085'); return;
            case '_': result.Append('\u00a0'); return;
            case 'L': result.Append('\u2028'); return;
            case 'P': result.Append('\u2029'); return;
            case 'x':
                AppendHexEscape(result, text, ref pos, 2, escape);
                return;
            case 'u':
                AppendHexEscape(result, text, ref pos, 4, escape);
                return;
            case 'U':
                AppendHexEscape(result, text, ref pos, 8, escape);
                return;
            default:
                // Preserve unknown escapes instead of silently changing Unity-authored data.
                result.Append('\\').Append(escape);
                return;
        }
    }

    private static void AppendHexEscape(StringBuilder result, string text, ref int pos, int digits,
        char escape)
    {
        if (pos + digits <= text.Length &&
            int.TryParse(text.AsSpan(pos, digits), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int codePoint) &&
            codePoint <= 0x10ffff && (codePoint < 0xd800 || codePoint > 0xdfff))
        {
            result.Append(char.ConvertFromUtf32(codePoint));
            pos += digits;
            return;
        }

        result.Append('\\').Append(escape);
    }
}
