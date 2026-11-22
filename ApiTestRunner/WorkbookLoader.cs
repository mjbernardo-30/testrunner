using System.Text.RegularExpressions;
using ApiTestRunner.Xlsx;

namespace ApiTestRunner;

/// <summary>
/// Reads the SA test workbook. Columns are found by header name (case/space-insensitive),
/// so SAs can reorder columns or add their own note columns without breaking anything.
/// </summary>
public static class WorkbookLoader
{
    private sealed record Row(int Number, Dictionary<string, string> Values)
    {
        public string Get(params string[] names)
        {
            foreach (var n in names)
                if (Values.TryGetValue(Norm(n), out var v)) return v.Trim();
            return "";
        }
    }

    public static TestSuite Load(string path, List<string> problems)
    {
        var sheets = XlsxReader.ReadAll(path);

        List<Row> Sheet(string name, bool required, params string[] requiredColumns)
        {
            if (!sheets.TryGetValue(name, out var grid))
            {
                if (required) problems.Add($"Sheet '{name}' is missing.");
                return new List<Row>();
            }
            var rows = ToRows(grid, out var headers);
            foreach (var col in requiredColumns)
                if (!headers.Contains(Norm(col)))
                    problems.Add($"Sheet '{name}' is missing the column '{col}'.");
            return rows;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Sheet("Settings", false, "Setting", "Value"))
        {
            var key = r.Get("Setting");
            if (key.Length > 0) settings[key] = r.Get("Value");
        }

        var envs = Sheet("Environments", true, "Environment", "Service", "Base URL")
            .Select(r => new EnvironmentService
            {
                Environment = r.Get("Environment"),
                Service = r.Get("Service"),
                BaseUrl = r.Get("Base URL", "BaseUrl"),
                ClientCertificate = r.Get("Client Certificate"),
                CertificatePassword = r.Get("Certificate Password"),
            })
            .Where(e => e.Environment.Length > 0 && e.BaseUrl.Length > 0)
            .ToList();

        var headers = Sheet("Headers", false, "Header Name", "Value")
            .Select(r => new NameValue { Environment = r.Get("Environment"), Name = r.Get("Header Name", "Header"), Value = r.Get("Value") })
            .Where(h => h.Name.Length > 0)
            .ToList();

        var variables = Sheet("Variables", false, "Variable Name", "Value")
            .Select(r => new NameValue { Environment = r.Get("Environment"), Name = r.Get("Variable Name", "Variable", "Name"), Value = r.Get("Value") })
            .Where(v => v.Name.Length > 0)
            .ToList();

        var steps = new List<StepDef>();
        foreach (var r in Sheet("TestCases", true, "Scenario", "Step", "Method", "Endpoint"))
        {
            var scenario = r.Get("Scenario");
            if (scenario.Length == 0) continue;
            var step = ParseInt(r.Get("Step"), r.Number, "TestCases", "Step", problems);
            var method = r.Get("Method").ToUpperInvariant();
            if (method.Length == 0) method = "GET";
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "SQL"))
                problems.Add($"TestCases row {r.Number}: Method '{method}' is not supported (use GET, POST, PUT, PATCH, DELETE or SQL).");

            steps.Add(new StepDef
            {
                SheetRow = r.Number,
                Run = !IsNo(r.Get("Run")),
                Scenario = scenario,
                ScenarioDescription = r.Get("Scenario Description", "Description"),
                Step = step,
                StepName = r.Get("Step Name"),
                Service = r.Get("Service"),
                Method = method,
                Endpoint = r.Get("Endpoint"),
                BaseUrlOverride = r.Get("Base URL Override", "Base URL"),
                RequestTemplate = r.Get("Request Template", "Request Body", "Body"),
                RetryCount = ParseIntOrDefault(r.Get("Retry Count", "Retries"), 0),
                RetryWaitSeconds = ParseIntOrDefault(r.Get("Retry Wait (sec)", "Retry Wait"), 2),
            });
        }

        // Duplicate step numbers inside a scenario are almost always a copy/paste mistake.
        foreach (var dup in steps.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"TestCases: scenario/step '{dup.Key.Replace("#", " step ")}' appears more than once (rows {string.Join(", ", dup.Select(d => d.SheetRow))}).");

        var stepKeys = steps.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void CheckKnown(string sheet, int row, string scenario, int step)
        {
            if (!stepKeys.Contains($"{scenario}#{step}"))
                problems.Add($"{sheet} row {row}: scenario '{scenario}' step {step} does not exist in TestCases.");
        }

        var overrides = new List<OverrideDef>();
        foreach (var r in Sheet("PayloadOverrides", false, "Scenario", "Step", "Field Path", "Value"))
        {
            if (r.Get("Scenario").Length == 0 || r.Get("Field Path").Length == 0) continue;
            var o = new OverrideDef
            {
                SheetRow = r.Number,
                Scenario = r.Get("Scenario"),
                Step = ParseInt(r.Get("Step"), r.Number, "PayloadOverrides", "Step", problems),
                FieldPath = r.Get("Field Path"),
                Value = r.Values.TryGetValue(Norm("Value"), out var raw) ? raw : "", // keep spaces as typed
            };
            CheckKnown("PayloadOverrides", r.Number, o.Scenario, o.Step);
            overrides.Add(o);
        }

        var assertions = new List<AssertionDef>();
        foreach (var r in Sheet("Assertions", false, "Scenario", "Step", "Field Path", "Check", "Expected"))
        {
            if (r.Get("Scenario").Length == 0 || r.Get("Field Path").Length == 0) continue;
            var a = new AssertionDef
            {
                SheetRow = r.Number,
                Scenario = r.Get("Scenario"),
                Step = ParseInt(r.Get("Step"), r.Number, "Assertions", "Step", problems),
                FieldPath = r.Get("Field Path"),
                Check = r.Get("Check").Length == 0 ? "equals" : r.Get("Check"),
                Expected = r.Get("Expected"),
                Description = r.Get("Description", "Notes"),
            };
            if (!AssertionEngine.IsKnownCheck(a.Check))
                problems.Add($"Assertions row {r.Number}: Check '{a.Check}' is not recognised. Valid: {string.Join(", ", AssertionEngine.CheckNames)}.");
            CheckKnown("Assertions", r.Number, a.Scenario, a.Step);
            assertions.Add(a);
        }

        var captures = new List<CaptureDef>();
        foreach (var r in Sheet("Capture", false, "Scenario", "Step", "Save As", "Field Path"))
        {
            if (r.Get("Scenario").Length == 0 || r.Get("Save As").Length == 0) continue;
            var c = new CaptureDef
            {
                SheetRow = r.Number,
                Scenario = r.Get("Scenario"),
                Step = ParseInt(r.Get("Step"), r.Number, "Capture", "Step", problems),
                SaveAs = r.Get("Save As").Trim('{', '}', ' '),
                FieldPath = r.Get("Field Path"),
            };
            CheckKnown("Capture", r.Number, c.Scenario, c.Step);
            captures.Add(c);
        }

        return new TestSuite
        {
            WorkbookPath = path,
            Settings = settings,
            Environments = envs,
            Headers = headers,
            Variables = variables,
            Steps = steps,
            Overrides = overrides,
            Assertions = assertions,
            Captures = captures,
        };
    }

    private static List<Row> ToRows(SortedDictionary<int, Dictionary<int, string>> grid, out HashSet<string> headerNames)
    {
        headerNames = new HashSet<string>();
        var rows = new List<Row>();
        if (grid.Count == 0) return rows;

        var headerRow = grid.First();
        var headers = headerRow.Value.ToDictionary(kv => kv.Key, kv => Norm(kv.Value));
        headerNames = headers.Values.ToHashSet();

        foreach (var (rowNum, cells) in grid.Skip(1))
        {
            var values = new Dictionary<string, string>();
            foreach (var (col, value) in cells)
                if (headers.TryGetValue(col, out var h) && !values.ContainsKey(h))
                    values[h] = value;
            if (values.Values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(new Row(rowNum, values));
        }
        return rows;
    }

    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    private static bool IsNo(string s) => s.Trim().ToUpperInvariant() is "N" or "NO" or "FALSE" or "0";

    private static int ParseInt(string s, int row, string sheet, string col, List<string> problems)
    {
        if (int.TryParse(s, out var i)) return i;
        if (decimal.TryParse(s, out var d) && d == Math.Floor(d)) return (int)d;
        problems.Add($"{sheet} row {row}: '{col}' must be a whole number (found '{s}').");
        return 0;
    }

    private static int ParseIntOrDefault(string s, int fallback) =>
        int.TryParse(s, out var i) ? i : decimal.TryParse(s, out var d) ? (int)d : fallback;
}
