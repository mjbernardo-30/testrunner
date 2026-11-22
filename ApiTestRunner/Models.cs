namespace ApiTestRunner;

public sealed class EnvironmentService
{
    public string Environment { get; init; } = "";
    public string Service { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    /// <summary>Optional, for two-way SSL: .pfx path or "thumbprint:XXXX".</summary>
    public string ClientCertificate { get; init; } = "";
    public string CertificatePassword { get; init; } = "";
}

/// <summary>Header / Variable rows: Environment is blank for "all environments".</summary>
public sealed class NameValue
{
    public string Environment { get; init; } = "";
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class StepDef
{
    public int SheetRow { get; init; }
    public bool Run { get; init; } = true;
    public string Scenario { get; init; } = "";
    public string ScenarioDescription { get; init; } = "";
    public int Step { get; init; }
    public string StepName { get; init; } = "";
    public string Service { get; init; } = "";
    public string Method { get; init; } = "GET";
    public string Endpoint { get; init; } = "";
    public string BaseUrlOverride { get; init; } = "";
    public string RequestTemplate { get; init; } = "";
    public int RetryCount { get; init; }
    public int RetryWaitSeconds { get; init; } = 2;

    public bool IsSetup => Scenario.StartsWith("_SETUP", StringComparison.OrdinalIgnoreCase);
    public string Key => $"{Scenario}#{Step}";
}

public sealed class OverrideDef
{
    public int SheetRow { get; init; }
    public string Scenario { get; init; } = "";
    public int Step { get; init; }
    public string FieldPath { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class AssertionDef
{
    public int SheetRow { get; init; }
    public string Scenario { get; init; } = "";
    public int Step { get; init; }
    public string FieldPath { get; init; } = "";
    public string Check { get; init; } = "";
    public string Expected { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class CaptureDef
{
    public int SheetRow { get; init; }
    public string Scenario { get; init; } = "";
    public int Step { get; init; }
    public string SaveAs { get; init; } = "";
    public string FieldPath { get; init; } = "";
}

public sealed class TestSuite
{
    public string WorkbookPath { get; init; } = "";
    public string WorkbookFolder => Path.GetDirectoryName(Path.GetFullPath(WorkbookPath))!;
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<EnvironmentService> Environments { get; init; } = new();
    public List<NameValue> Headers { get; init; } = new();
    public List<NameValue> Variables { get; init; } = new();
    public List<StepDef> Steps { get; init; } = new();
    public List<OverrideDef> Overrides { get; init; } = new();
    public List<AssertionDef> Assertions { get; init; } = new();
    public List<CaptureDef> Captures { get; init; } = new();

    // ---- Indexed lookups (built once; O(1) per step instead of scanning every row) ----
    private ILookup<string, AssertionDef>? _assertionIdx;
    private ILookup<string, CaptureDef>? _captureIdx;
    private ILookup<string, OverrideDef>? _overrideIdx;

    private static string Key(string scenario, int step) => $"{scenario}#{step}";

    /// <summary>Builds the lookups. Call once before running (not thread-safe to build lazily).</summary>
    public void BuildIndexes()
    {
        _assertionIdx = Assertions.ToLookup(a => Key(a.Scenario, a.Step), StringComparer.OrdinalIgnoreCase);
        _captureIdx = Captures.ToLookup(c => Key(c.Scenario, c.Step), StringComparer.OrdinalIgnoreCase);
        _overrideIdx = Overrides.ToLookup(o => Key(o.Scenario, o.Step), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<AssertionDef> AssertionsFor(StepDef s) => (_assertionIdx ?? throw NotBuilt())[s.Key];
    public IEnumerable<CaptureDef> CapturesFor(StepDef s) => (_captureIdx ?? throw NotBuilt())[s.Key];
    public IEnumerable<OverrideDef> OverridesFor(StepDef s) => (_overrideIdx ?? throw NotBuilt())[s.Key];
    private static InvalidOperationException NotBuilt() => new("TestSuite.BuildIndexes() must be called first.");

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    public bool SettingBool(string key, bool fallback) =>
        Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim().ToUpperInvariant() is "Y" or "YES" or "TRUE" or "1"
            : fallback;

    public int SettingInt(string key, int fallback) =>
        Settings.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : fallback;
}

// ----------------------------- Results -----------------------------

public enum Outcome { Pass, Fail, Error, Skipped }

public sealed class AssertionResult
{
    public required AssertionDef Def { get; init; }
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public bool Passed { get; init; }
    public string Message { get; init; } = "";
}

public sealed class StepResult
{
    public required StepDef Def { get; init; }
    public Outcome Outcome { get; set; }
    public DateTime StartedAt { get; set; }
    public string Method { get; set; } = "";
    public string Url { get; set; } = "";
    public int? HttpStatus { get; set; }
    public long DurationMs { get; set; }
    public int Attempts { get; set; }
    public string RequestHeaders { get; set; } = "";
    public string RequestBody { get; set; } = "";
    public string ResponseHeaders { get; set; } = "";
    public string ResponseBody { get; set; } = "";
    public List<AssertionResult> Assertions { get; set; } = new();
    public Dictionary<string, string> Captured { get; } = new();
    public List<string> Notes { get; } = new();
    public string Error { get; set; } = "";

    /// <summary>Failed checks first (most useful), then any other error such as a missing capture.</summary>
    public string FailureReason => _frozenReason ??
        string.Join("; ", Assertions.Where(a => !a.Passed)
            .Select(a => $"{a.Def.FieldPath} {a.Def.Check} '{a.Expected}' but was '{a.Actual}'")
            .Append(Error)
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    private string? _frozenReason;
    private int? _checksPassed, _checksTotal;
    public int ChecksPassed => _checksPassed ?? Assertions.Count(a => a.Passed);
    public int ChecksTotal => _checksTotal ?? Assertions.Count;

    /// <summary>Not run because the whole run was cancelled (Ctrl+C) - never counted as a failure.</summary>
    public bool CancelledStep { get; set; }

    /// <summary>True while request/response bodies and individual check results are still held in memory.</summary>
    public bool HasDetail { get; private set; } = true;

    /// <summary>
    /// Drops the heavy parts (bodies, headers, per-check results) once they have been written to disk,
    /// keeping only what the summary needs. Keeps memory flat on long runs.
    /// </summary>
    public void Compact()
    {
        if (!HasDetail) return;
        _frozenReason = FailureReason;
        _checksPassed = ChecksPassed;
        _checksTotal = ChecksTotal;
        RequestHeaders = RequestBody = ResponseHeaders = ResponseBody = "";
        Assertions = new();
        HasDetail = false;
    }
}

public sealed class ScenarioResult
{
    public string Scenario { get; init; } = "";
    public string Description { get; init; } = "";
    public List<StepResult> Steps { get; } = new();
    public long DurationMs => Steps.Sum(s => s.DurationMs);

    public Outcome Outcome
    {
        get
        {
            // Steps switched off with Run = N don't affect the scenario result.
            var active = Steps.Where(s => s.Def.Run).ToList();
            if (active.Count == 0 || active.All(s => s.Outcome == Outcome.Skipped)) return Outcome.Skipped;
            if (active.Any(s => s.Outcome == Outcome.Error)) return Outcome.Error;
            // Interrupted by Ctrl+C before finishing: incomplete, not failed (unless a real failure happened first).
            if (active.Any(s => s.CancelledStep) && !active.Any(s => s.Outcome == Outcome.Fail)) return Outcome.Skipped;
            if (active.Any(s => s.Outcome is Outcome.Fail or Outcome.Skipped)) return Outcome.Fail;
            return Outcome.Pass;
        }
    }
}

public sealed class RunResult
{
    public string RunId { get; init; } = "";
    public string Environment { get; init; } = "";
    public string WorkbookPath { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public bool Cancelled { get; set; }
    public int MaxParallel { get; set; } = 1;
    /// <summary>Total step results produced (kept even when individual results are compacted).</summary>
    public string ResultsFolder { get; set; } = "";
    public List<ScenarioResult> Scenarios { get; } = new();
}
