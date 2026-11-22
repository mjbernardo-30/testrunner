using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiTestRunner;

/// <summary>
/// SA-friendly field paths:  ResponseDetails.OrderResultList[0].OrderReferenceNo
/// A leading "$." is accepted too. Property names match case-insensitively if no exact match.
/// </summary>
public static class JsonPathUtil
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private abstract record Token;
    private sealed record Prop(string Name) : Token;
    private sealed record Index(int I) : Token;
    private sealed record Filter(string Field, string Value) : Token;   // [cdvNo=137]

    private static List<Token> Parse(string path)
    {
        path = path.Trim();
        if (path.StartsWith("$.")) path = path[2..];
        else if (path == "$") path = "";
        var tokens = new List<Token>();
        var name = new System.Text.StringBuilder();
        void FlushName()
        {
            if (name.Length > 0) tokens.Add(new Prop(name.ToString().Trim()));
            name.Clear();
        }
        int i = 0;
        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.') { FlushName(); i++; continue; }
            if (c == '[')
            {
                FlushName();
                int end = path.IndexOf(']', i);
                if (end < 0) { name.Append(path[i..]); break; }
                var inside = path[(i + 1)..end].Trim();
                if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out var idx))
                    tokens.Add(new Index(idx));
                else if (inside.IndexOf('=') is var eq && eq > 0)
                    tokens.Add(new Filter(inside[..eq].Trim(), inside[(eq + 1)..].Trim().Trim('\'', '"')));
                i = end + 1;
                continue;
            }
            name.Append(c);
            i++;
        }
        FlushName();
        return tokens;
    }

    /// <summary>Gets a node. found=false when the path doesn't exist; node may be null for JSON null.</summary>
    public static bool TryGet(JsonNode? root, string path, out JsonNode? node)
    {
        node = root;
        foreach (var t in Parse(path))
        {
            switch (t)
            {
                case Prop p when node is JsonObject obj:
                    var key = FindKey(obj, p.Name);
                    if (key is null) { node = null; return false; }
                    node = obj[key];
                    break;
                case Index i when node is JsonArray arr:
                    if (i.I >= arr.Count) { node = null; return false; }
                    node = arr[i.I];
                    break;
                case Prop p when node is JsonArray list &&
                                 (p.Name.Equals("Count", StringComparison.OrdinalIgnoreCase) ||
                                  p.Name.Equals("Length", StringComparison.OrdinalIgnoreCase)):
                    node = JsonValue.Create(list.Count);   // Results.Count → number of items
                    break;
                case Filter f when node is JsonArray arr:
                    // First item in the list whose field equals the value, e.g. searchResults[cdvNo=137]
                    var match = arr.FirstOrDefault(item => item is JsonObject o && FindKey(o, f.Field) is string k && SameValue(ToText(o[k]), f.Value));
                    if (match is null) { node = null; return false; }
                    node = match;
                    break;
                default:
                    node = null;
                    return false;
            }
        }
        return true;
    }

    /// <summary>Sets a value, creating missing objects on the way. Returns error text or null.</summary>
    public static string? Set(JsonNode root, string path, JsonNode? value, bool remove)
    {
        var tokens = Parse(path);
        if (tokens.Count == 0) return "Field Path is empty.";
        JsonNode current = root;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            var t = tokens[i];
            var next = tokens[i + 1];
            JsonNode? child;
            switch (t)
            {
                case Prop p when current is JsonObject obj:
                    var key = FindKey(obj, p.Name);
                    child = key is null ? null : obj[key];
                    if (child is null)
                    {
                        if (remove) return null; // nothing to remove
                        child = next is Index ? new JsonArray() : new JsonObject();
                        obj[key ?? p.Name] = child;
                    }
                    break;
                case Index ix when current is JsonArray arr:
                    if (ix.I >= arr.Count) return remove ? null : $"List index [{ix.I}] does not exist in the template (list has {arr.Count} item(s)).";
                    child = arr[ix.I] ?? (arr[ix.I] = new JsonObject());
                    break;
                default:
                    return $"Cannot navigate '{path}' - part of the path is not an object/list in the template.";
            }
            current = child!;
        }

        switch (tokens[^1])
        {
            case Prop p when current is JsonObject obj:
                var key = FindKey(obj, p.Name);
                if (remove) { if (key is not null) obj.Remove(key); return null; }
                obj[key ?? p.Name] = value;
                return null;
            case Index ix when current is JsonArray arr:
                if (ix.I >= arr.Count) return remove ? null : $"List index [{ix.I}] does not exist in the template.";
                if (remove) { arr.RemoveAt(ix.I); return null; }
                arr[ix.I] = value;
                return null;
            default:
                return $"Cannot set '{path}' - parent is not an object/list.";
        }
    }

    private static bool SameValue(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        (decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
         decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y);

    private static string? FindKey(JsonObject obj, string name)
    {
        if (obj.ContainsKey(name)) return name;
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return null;
    }

    /// <summary>Text form used for comparisons: strings unquoted, numbers/bools raw, null → "null", objects compact JSON.</summary>
    public static string ToText(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => node.ToJsonString(Compact),
    };

    public static string KindName(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray a => $"list ({a.Count} items)",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "text",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "true/false",
            _ => "value",
        },
        _ => "value",
    };

    /// <summary>Every field path in a document (used by discover mode).</summary>
    public static IEnumerable<(string Path, string Value, string Kind)> Flatten(JsonNode? node, string prefix = "")
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.Count == 0 && prefix.Length > 0) yield return (prefix, "{}", "object");
                foreach (var kv in obj)
                    foreach (var x in Flatten(kv.Value, prefix.Length == 0 ? kv.Key : $"{prefix}.{kv.Key}"))
                        yield return x;
                break;
            case JsonArray arr:
                yield return (prefix, $"{arr.Count} item(s)", "list");
                for (int i = 0; i < arr.Count; i++)
                    foreach (var x in Flatten(arr[i], $"{prefix}[{i}]"))
                        yield return x;
                break;
            default:
                yield return (prefix, ToText(node), KindName(node));
                break;
        }
    }

    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
    }

    public static string PrettyOrRaw(string? text)
    {
        var n = TryParse(text);
        return n is null ? text ?? "" : n.ToJsonString(Pretty);
    }
}
