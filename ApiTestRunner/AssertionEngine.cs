using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ApiTestRunner;

/// <summary>What a step returned, in a shape assertions and captures can read.</summary>
public sealed class StepResponse
{
    public int? HttpStatus { get; init; }
    public long DurationMs { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cookies set by the response (Set-Cookie), name → value.</summary>
    public Dictionary<string, string> Cookies { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string BodyText { get; init; } = "";
    public JsonNode? BodyJson { get; init; }

    /// <summary>
    /// Reads a value by SA field path. Special paths: "HTTP Status", "Response Time (ms)",
    /// "Header:Name", "Cookie:Name", "Body" (raw text). Everything else is a JSON path into the body.
    /// </summary>
    public (bool Found, JsonNode? Node, string Text) Read(string fieldPath)
    {
        var p = fieldPath.Trim();
        var norm = WorkbookLoader.Norm(p);
        if (norm is "httpstatus" or "statuscode" or "httpstatuscode")
            return HttpStatus is int s ? (true, JsonValue.Create(s), s.ToString(CultureInfo.InvariantCulture)) : (false, null, "");
        if (norm is "responsetimems" or "responsetime" or "durationms")
            return (true, JsonValue.Create(DurationMs), DurationMs.ToString(CultureInfo.InvariantCulture));
        if (norm is "body" or "responsebody")
            return (true, JsonValue.Create(BodyText), BodyText);
        if (p.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            var name = p[7..].Trim();
            return Cookies.TryGetValue(name, out var cv) ? (true, JsonValue.Create(cv), cv) : (false, null, "");
        }
        if (p.StartsWith("header:", StringComparison.OrdinalIgnoreCase))
        {
            var name = p[7..].Trim();
            return Headers.TryGetValue(name, out var hv) ? (true, JsonValue.Create(hv), hv) : (false, null, "");
        }
        if (BodyJson is null) return (false, null, "");
        return JsonPathUtil.TryGet(BodyJson, p, out var node) ? (true, node, JsonPathUtil.ToText(node)) : (false, null, "");
    }
}

public static class AssertionEngine
{
    private static readonly Dictionary<string, string> Checks = new()
    {
        ["equals"] = "equals",
        ["notequals"] = "not equals",
        ["equalsignorecase"] = "equals (ignore case)",
        ["contains"] = "contains",
        ["notcontains"] = "not contains",
        ["startswith"] = "starts with",
        ["endswith"] = "ends with",
        ["isempty"] = "is empty",
        ["isnotempty"] = "is not empty",
        ["exists"] = "exists",
        ["notexists"] = "not exists",
        ["countequals"] = "count equals",
        ["greaterthan"] = "greater than",
        ["greaterorequal"] = "greater or equal",
        ["lessthan"] = "less than",
        ["lessorequal"] = "less or equal",
        ["matchespattern"] = "matches pattern",
        ["oneof"] = "one of",
    };

    public static IEnumerable<string> CheckNames => Checks.Values;
    public static bool IsKnownCheck(string check) => Checks.ContainsKey(WorkbookLoader.Norm(check));

    public static AssertionResult Evaluate(AssertionDef def, StepResponse resp, VariableContext vars)
    {
        var unresolved = new List<string>();
        var expected = vars.Resolve(def.Expected, EscapeMode.None, unresolved);
        if (unresolved.Count > 0)
            return Fail(def, expected, "", $"Unknown variable(s) in Expected: {string.Join(", ", unresolved.Select(u => "{{" + u + "}}"))}");

        var (found, node, actual) = resp.Read(vars.Resolve(def.FieldPath, EscapeMode.None));
        var check = WorkbookLoader.Norm(def.Check);
        string shownActual = found ? actual : "(field not found)";

        bool IsEmptyValue() => !found || node is null || (node is JsonArray a && a.Count == 0) || (node is JsonObject o && o.Count == 0) || (node is JsonValue && actual.Length == 0);

        bool ok;
        string msg = "";
        switch (check)
        {
            case "exists": ok = found; break;
            case "notexists": ok = !found; break;
            case "isempty": ok = IsEmptyValue(); break;
            case "isnotempty": ok = !IsEmptyValue(); break;
            default:
                if (!found) return Fail(def, expected, shownActual, $"Field '{def.FieldPath}' was not found in the response.");
                switch (check)
                {
                    case "equals": ok = ValueEquals(actual, expected, ignoreCase: false); break;
                    case "notequals": ok = !ValueEquals(actual, expected, ignoreCase: false); break;
                    case "equalsignorecase": ok = ValueEquals(actual, expected, ignoreCase: true); break;
                    case "contains": ok = actual.Contains(expected, StringComparison.OrdinalIgnoreCase); break;
                    case "notcontains": ok = !actual.Contains(expected, StringComparison.OrdinalIgnoreCase); break;
                    case "startswith": ok = actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase); break;
                    case "endswith": ok = actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase); break;
                    case "oneof":
                        ok = expected.Split(new[] { ',', '|', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                     .Any(e => ValueEquals(actual, e, ignoreCase: false));
                        break;
                    case "matchespattern":
                        try { ok = Regex.IsMatch(actual, expected, RegexOptions.None, TimeSpan.FromSeconds(2)); }
                        catch (ArgumentException ex) { return Fail(def, expected, shownActual, $"Invalid pattern: {ex.Message}"); }
                        break;
                    case "countequals":
                    {
                        int? count = node switch { JsonArray a => a.Count, JsonObject o => o.Count, _ => null };
                        if (count is null) return Fail(def, expected, shownActual, "'count equals' needs a list field.");
                        shownActual = count.Value.ToString(CultureInfo.InvariantCulture);
                        ok = int.TryParse(expected, out var exp) && exp == count;
                        break;
                    }
                    case "greaterthan":
                    case "greaterorequal":
                    case "lessthan":
                    case "lessorequal":
                    {
                        if (!TryNum(actual, out var a) || !TryNum(expected, out var e))
                            return Fail(def, expected, shownActual, "Both actual and expected must be numbers for this check.");
                        ok = check switch
                        {
                            "greaterthan" => a > e,
                            "greaterorequal" => a >= e,
                            "lessthan" => a < e,
                            _ => a <= e,
                        };
                        break;
                    }
                    default:
                        return Fail(def, expected, shownActual, $"Unknown check '{def.Check}'.");
                }
                break;
        }

        if (!ok && msg.Length == 0) msg = $"Expected {Checks.GetValueOrDefault(check, def.Check)} '{expected}'";
        return new AssertionResult { Def = def, Expected = expected, Actual = shownActual, Passed = ok, Message = ok ? "" : msg };
    }

    /// <summary>Text equality, but 1000 == 1000.00 when both sides are numbers.</summary>
    private static bool ValueEquals(string actual, string expected, bool ignoreCase)
    {
        if (string.Equals(actual, expected, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return true;
        // Keep codes like "0000" vs "0" distinct: only treat as numbers if neither has a leading zero.
        if (!HasLeadingZero(actual) && !HasLeadingZero(expected) && TryNum(actual, out var a) && TryNum(expected, out var e)) return a == e;
        return false;
    }

    private static bool HasLeadingZero(string s) => s.Length > 1 && s[0] == '0' && s[1] != '.';

    private static bool TryNum(string s, out decimal d) =>
        decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    private static AssertionResult Fail(AssertionDef def, string expected, string actual, string message) =>
        new() { Def = def, Expected = expected, Actual = actual, Passed = false, Message = message };
}
