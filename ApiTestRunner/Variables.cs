using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApiTestRunner;

public enum EscapeMode { None, Json, Url }

/// <summary>
/// Resolves {{placeholders}}. Lookup order: values captured in this scenario →
/// values captured by _SETUP scenarios → Variables sheet (environment-specific first) → built-ins.
/// </summary>
public sealed class VariableContext
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, string> _sheet;                  // read-only after construction
    private readonly ConcurrentDictionary<string, string> _global;       // shared: _SETUP captures
    private Dictionary<string, string> _scenario = new(StringComparer.OrdinalIgnoreCase); // this scenario only

    public string RunId { get; }
    public string Environment { get; }

    public VariableContext(TestSuite suite, string environment, string runId)
    {
        Environment = environment;
        RunId = runId;
        _sheet = new(StringComparer.OrdinalIgnoreCase);
        _global = new(StringComparer.OrdinalIgnoreCase);
        // "All environments" rows first, then environment-specific rows win.
        foreach (var v in suite.Variables.Where(v => v.Environment.Length == 0)) _sheet[v.Name] = v.Value;
        foreach (var v in suite.Variables.Where(v => v.Environment.Equals(environment, StringComparison.OrdinalIgnoreCase))) _sheet[v.Name] = v.Value;
    }

    private VariableContext(VariableContext parent)
    {
        Environment = parent.Environment;
        RunId = parent.RunId;
        _sheet = parent._sheet;
        _global = parent._global;
    }

    /// <summary>A fresh context for one scenario: shares Variables-sheet and _SETUP values, but its own captures.</summary>
    public VariableContext ForScenario() => new(this);

    public void BeginScenario() => _scenario = new(StringComparer.OrdinalIgnoreCase);
    public void SetScenario(string name, string value) => _scenario[name] = value;
    public void SetGlobal(string name, string value) => _global[name] = value;

    public IReadOnlyDictionary<string, string> ScenarioValues => _scenario;

    /// <summary>Replaces placeholders. Unknown names are left in place and reported via <paramref name="unresolved"/>.</summary>
    public string Resolve(string? text, EscapeMode mode, ICollection<string>? unresolved = null, int depth = 0)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Placeholder.Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            if (!TryGet(name, out var value, depth))
            {
                unresolved?.Add(name);
                return m.Value;
            }
            return mode switch
            {
                EscapeMode.Json => JsonEscape(value),
                EscapeMode.Url => Uri.EscapeDataString(value),
                _ => value,
            };
        });
    }

    public bool TryGet(string name, out string value, int depth = 0)
    {
        if (_scenario.TryGetValue(name, out value!)) return true;
        if (_global.TryGetValue(name, out value!)) return true;
        if (_sheet.TryGetValue(name, out var raw))
        {
            // Variables can reference other variables / built-ins (e.g. {{$env:SIT_PASSWORD}}).
            value = depth < 5 ? Resolve(raw, EscapeMode.None, null, depth + 1) : raw;
            return true;
        }
        return TryBuiltIn(name, out value);
    }

    private bool TryBuiltIn(string name, out string value)
    {
        var now = DateTime.Now;
        var parts = name.Split(':', 3);
        var key = parts[0].ToLowerInvariant();
        value = "";
        switch (key)
        {
            case "$guid": value = Guid.NewGuid().ToString(); return true;
            case "$guidn": value = Guid.NewGuid().ToString("N"); return true;
            case "$timestamp":
                // Never repeats, even with parallel scenarios in the same millisecond.
                value = DateTimeOffset.FromUnixTimeMilliseconds(NextUniqueMs()).ToLocalTime().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                return true;
            case "$unique":
            {
                // {{$unique}} → 13 digits, {{$unique:12}} → exactly 12 digits. Guaranteed unique within a run
                // (also in parallel) and across runs: milliseconds since 2020-01-01, never handed out twice.
                int len = parts.Length > 1 && int.TryParse(parts[1], out var ul) ? ul : 13;
                if (len < 12 || len > 20) return false; // reported as unknown variable
                var ms = NextUniqueMs() - UniqueEpochMs;
                value = ms.ToString(CultureInfo.InvariantCulture).PadLeft(len, '0');
                return true;
            }
            case "$now": value = now.ToString(parts.Length > 1 ? string.Join(':', parts[1..]) : "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture); return true;
            case "$today": value = now.ToString(parts.Length > 1 ? string.Join(':', parts[1..]) : "yyyy-MM-dd", CultureInfo.InvariantCulture); return true;
            case "$runid": value = RunId; return true;
            case "$environment": value = Environment; return true;
            case "$random":
            {
                int len = parts.Length > 1 && int.TryParse(parts[1], out var l) ? Math.Clamp(l, 1, 30) : 6;
                value = string.Concat(Enumerable.Range(0, len).Select(_ => (char)('0' + Random.Shared.Next(10))));
                return true;
            }
            case "$dateadd":
            {
                // {{$dateAdd:7}} or {{$dateAdd:7:yyyyMMdd}}  (days from today)
                if (parts.Length < 2 || !int.TryParse(parts[1], out var days)) return false;
                value = now.Date.AddDays(days).ToString(parts.Length > 2 ? parts[2] : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return true;
            }
            case "$env":
            {
                if (parts.Length < 2) return false;
                var v = System.Environment.GetEnvironmentVariable(parts[1]);
                if (v is null) return false;
                value = v;
                return true;
            }
        }
        return false;
    }

    // ---- Collision-free time-based values (shared by all scenarios / threads) ----
    private static long _lastUniqueMs;
    private static readonly long UniqueEpochMs = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    /// <summary>Current time in ms, but strictly increasing: if two callers ask in the same ms, the second gets +1.</summary>
    private static long NextUniqueMs()
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastUniqueMs);
            var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), last + 1);
            if (Interlocked.CompareExchange(ref _lastUniqueMs, next, last) == last) return next;
        }
    }

    public static string JsonEscape(string value)
    {
        var s = JsonSerializer.Serialize(value, JsonOpts);
        return s[1..^1];
    }

    public static IEnumerable<string> FindPlaceholders(string? text) =>
        string.IsNullOrEmpty(text) ? Enumerable.Empty<string>() : Placeholder.Matches(text).Select(m => m.Groups[1].Value.Trim());

    public static Regex PlaceholderRegex => Placeholder;
}
