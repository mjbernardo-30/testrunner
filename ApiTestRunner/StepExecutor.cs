using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ApiTestRunner;

/// <summary>Builds and sends one step (HTTP call or SQL query) and returns the raw response.</summary>
public sealed class StepExecutor
{
    private readonly TestSuite _suite;
    private readonly HttpClientProvider _clients;
    private readonly string _environment;
    private readonly string _templatesFolder;

    public StepExecutor(TestSuite suite, HttpClientProvider clients, string environment)
    {
        _suite = suite;
        _clients = clients;
        _environment = environment;
        var folder = suite.Setting("TemplatesFolder", "Templates");
        _templatesFolder = Path.IsPathRooted(folder) ? folder : Path.Combine(suite.WorkbookFolder, folder);
    }

    public sealed class PreparedRequest
    {
        public string Method { get; set; } = "";
        public string Url { get; set; } = "";
        public List<KeyValuePair<string, string>> Headers { get; } = new();
        public string? Body { get; set; }
        public string ClientCertificate { get; set; } = "";
        public string CertificatePassword { get; set; } = "";
        public List<string> Notes { get; } = new();
    }

    /// <summary>Resolves URL, headers and body. Throws StepConfigException with an SA-readable message on problems.</summary>
    public PreparedRequest Prepare(StepDef step, VariableContext vars)
    {
        var req = new PreparedRequest { Method = step.Method };
        var overrides = _suite.OverridesFor(step).ToList();

        // ---------- URL / connection ----------
        var unresolved = new List<string>();
        if (step.Method == "SQL")
        {
            req.Url = ResolveBase(step, vars, req); // connection string lives in Environments sheet
        }
        else
        {
            var endpoint = vars.Resolve(step.Endpoint, EscapeMode.Url, unresolved);
            if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                req.Url = endpoint;
            else
                req.Url = ResolveBase(step, vars, req).TrimEnd('/') + "/" + endpoint.TrimStart('/');
            ThrowIfUnresolved(unresolved, "Endpoint");
        }

        // ---------- Headers ----------
        if (step.Method != "SQL")
        {
            var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in _suite.Headers.Where(h => h.Environment.Length == 0)) headerMap[h.Name] = h.Value;
            foreach (var h in _suite.Headers.Where(h => h.Environment.Equals(_environment, StringComparison.OrdinalIgnoreCase))) headerMap[h.Name] = h.Value;
            foreach (var o in overrides.Where(o => o.FieldPath.StartsWith("header:", StringComparison.OrdinalIgnoreCase)))
            {
                var name = o.FieldPath[7..].Trim();
                if (o.Value.Trim().Equals("<REMOVE>", StringComparison.OrdinalIgnoreCase)) headerMap.Remove(name);
                else headerMap[name] = o.Value;
            }
            foreach (var (name, raw) in headerMap)
            {
                var missing = new List<string>();
                var value = vars.Resolve(raw, EscapeMode.None, missing);
                if (missing.Count > 0)
                {
                    // e.g. Authorization: Bearer {{accessToken}} before the token step has run.
                    req.Notes.Add($"Header '{name}' not sent (no value yet for {string.Join(", ", missing.Select(m => "{{" + m + "}}"))}).");
                    continue;
                }
                req.Headers.Add(new(name, value));
            }
        }

        // ---------- Body ----------
        var template = LoadTemplate(step);
        if (template is null)
        {
            if (overrides.Any(o => !IsHeaderOverride(o)))
                throw new StepConfigException("PayloadOverrides exist for this step but it has no Request Template.");
            return req;
        }

        if (step.Method == "SQL")
        {
            req.Body = template; // placeholders become SQL parameters at execution time
            return req;
        }

        var resolved = vars.Resolve(template, EscapeMode.Json, unresolved);
        ThrowIfUnresolved(unresolved, "Request Template");

        var bodyOverrides = overrides.Where(o => !IsHeaderOverride(o)).ToList();
        var json = JsonPathUtil.TryParse(resolved);
        if (json is null)
        {
            if (bodyOverrides.Count > 0)
                throw new StepConfigException("Request Template is not valid JSON, so PayloadOverrides cannot be applied.");
            req.Body = resolved; // non-JSON body, send as-is
            return req;
        }

        foreach (var o in bodyOverrides)
        {
            var missing = new List<string>();
            var raw = vars.Resolve(o.Value, EscapeMode.None, missing);
            if (missing.Count > 0)
                throw new StepConfigException($"PayloadOverrides row {o.SheetRow}: unknown variable(s) {string.Join(", ", missing.Select(m => "{{" + m + "}}"))}.");

            JsonPathUtil.TryGet(json, o.FieldPath, out var existing);
            var value = ConvertOverride(raw, existing, out var remove, o.SheetRow);
            var err = JsonPathUtil.Set(json, o.FieldPath, value, remove);
            if (err is not null) throw new StepConfigException($"PayloadOverrides row {o.SheetRow} ('{o.FieldPath}'): {err}");
        }
        req.Body = json.ToJsonString(JsonPathUtil.Pretty);
        return req;
    }

    public async Task<StepResponse> SendAsync(PreparedRequest req, VariableContext vars, CancellationToken ct)
    {
        if (req.Method == "SQL") return await SqlStep.ExecuteAsync(req, vars, ct);
        var http = _clients.Get(req.ClientCertificate, req.CertificatePassword);

        using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
        string? contentType = null;
        foreach (var (name, value) in req.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = value; continue; }
            msg.Headers.TryAddWithoutValidation(name, value);
        }
        if (req.Body is not null)
        {
            msg.Content = new StringContent(req.Body, Encoding.UTF8);
            msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json; charset=utf-8");
        }

        var sw = Stopwatch.StartNew();
        using var resp = await http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in resp.Headers) headers[h.Key] = string.Join(", ", h.Value);
        foreach (var h in resp.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);

        // Set-Cookie: read each header separately (cookie expiry dates contain commas).
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var sc in setCookies)
            {
                var first = sc.Split(';', 2)[0];
                var eq = first.IndexOf('=');
                if (eq > 0) cookies[first[..eq].Trim()] = Uri.UnescapeDataString(first[(eq + 1)..].Trim().Trim('"'));
            }
            headers["Set-Cookie"] = string.Join("\n", setCookies);
        }

        return new StepResponse
        {
            HttpStatus = (int)resp.StatusCode,
            DurationMs = sw.ElapsedMilliseconds,
            Headers = headers,
            Cookies = cookies,
            BodyText = body,
            BodyJson = JsonPathUtil.TryParse(body),
        };
    }

    private string ResolveBase(StepDef step, VariableContext vars, PreparedRequest req)
    {
        string raw;
        // Client certificate follows the Service, even when the URL is overridden.
        var svc = step.Service.Length == 0 ? null : _suite.Environments.FirstOrDefault(e =>
            e.Environment.Equals(_environment, StringComparison.OrdinalIgnoreCase) && e.Service.Equals(step.Service, StringComparison.OrdinalIgnoreCase));
        void ApplyCert(EnvironmentService? s)
        {
            if (s is null || s.ClientCertificate.Length == 0) return;
            var missing = new List<string>();
            req.ClientCertificate = vars.Resolve(s.ClientCertificate, EscapeMode.None, missing);
            req.CertificatePassword = vars.Resolve(s.CertificatePassword, EscapeMode.None, missing);
            ThrowIfUnresolved(missing, $"Client Certificate settings of service '{s.Service}'");
        }
        if (step.BaseUrlOverride.Length > 0)
        {
            raw = step.BaseUrlOverride;
            ApplyCert(svc);
        }
        else
        {
            var candidates = _suite.Environments.Where(e => e.Environment.Equals(_environment, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0)
                throw new StepConfigException($"Environment '{_environment}' is not defined in the Environments sheet.");
            EnvironmentService? match;
            if (step.Service.Length > 0)
            {
                match = candidates.FirstOrDefault(c => c.Service.Equals(step.Service, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    throw new StepConfigException($"Service '{step.Service}' has no Base URL for environment '{_environment}' in the Environments sheet.");
            }
            else if (candidates.Count == 1) match = candidates[0];
            else throw new StepConfigException($"Fill the Service column - environment '{_environment}' has more than one service.");
            raw = match.BaseUrl;
            ApplyCert(match);
        }
        var unresolved = new List<string>();
        var value = vars.Resolve(raw, EscapeMode.None, unresolved);
        ThrowIfUnresolved(unresolved, "Base URL");
        return value;
    }

    private string? LoadTemplate(StepDef step)
    {
        var t = step.RequestTemplate.Trim();
        if (t.Length == 0) return null;
        if (t.StartsWith('{') || t.StartsWith('[')) return step.RequestTemplate; // inline JSON
        var looksLikeFile = Path.HasExtension(t) && t.IndexOfAny(new[] { '\n', ' ' }) < 0;
        if (looksLikeFile)
        {
            var path = Path.IsPathRooted(t) ? t : Path.Combine(_templatesFolder, t);
            if (!File.Exists(path)) throw new StepConfigException($"Request Template file not found: {path}");
            return File.ReadAllText(path);
        }
        if (step.Method == "SQL") return step.RequestTemplate; // inline SQL
        throw new StepConfigException($"Request Template '{t}' is neither a file name (e.g. buy.json) nor inline JSON.");
    }

    private static bool IsHeaderOverride(OverrideDef o) => o.FieldPath.StartsWith("header:", StringComparison.OrdinalIgnoreCase);

    private static JsonNode? ConvertOverride(string raw, JsonNode? existing, out bool remove, int row)
    {
        remove = false;
        var trimmed = raw.Trim();
        switch (trimmed.ToUpperInvariant())
        {
            case "<REMOVE>": remove = true; return null;
            case "<NULL>": return null;
            case "<EMPTY>": return JsonValue.Create("");
            case "<TRUE>": return JsonValue.Create(true);
            case "<FALSE>": return JsonValue.Create(false);
        }
        if (trimmed.StartsWith("<JSON>", StringComparison.OrdinalIgnoreCase))
        {
            var node = JsonPathUtil.TryParse(trimmed[6..]);
            return node ?? throw new StepConfigException($"PayloadOverrides row {row}: value after <JSON> is not valid JSON.");
        }
        if (trimmed.StartsWith("<NUMBER>", StringComparison.OrdinalIgnoreCase))
        {
            return decimal.TryParse(trimmed[8..], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? JsonValue.Create(n)
                : throw new StepConfigException($"PayloadOverrides row {row}: value after <NUMBER> is not a number.");
        }

        // Keep the data type the template already uses for this field.
        if (existing is JsonValue ev)
        {
            var kind = ev.GetValueKind();
            if (kind == System.Text.Json.JsonValueKind.Number && decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                return JsonValue.Create(num);
            if (kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False && bool.TryParse(raw, out var b))
                return JsonValue.Create(b);
        }
        return JsonValue.Create(raw);
    }

    private static void ThrowIfUnresolved(List<string> unresolved, string where)
    {
        var envVars = unresolved.Where(u => u.StartsWith("$env:", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (envVars.Count > 0)
            throw new StepConfigException($"Windows environment variable(s) not set on this PC: {string.Join(", ", envVars.Select(e => e[5..]))} (needed by {where}). " +
                                          $"Set it once in cmd with:  setx {envVars[0][5..]} \"value\"  then open a new cmd window.");
        if (unresolved.Count > 0)
            throw new StepConfigException($"Unknown variable(s) in {where}: {string.Join(", ", unresolved.Distinct().Select(u => "{{" + u + "}}"))}. " +
                                          "Define them in the Variables sheet or capture them in an earlier step.");
    }
}

public sealed class StepConfigException(string message) : Exception(message);
