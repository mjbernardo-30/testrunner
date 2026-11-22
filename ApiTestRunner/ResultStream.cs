using System.Text;
using System.Text.RegularExpressions;

namespace ApiTestRunner;

public enum PayloadMode { All, Failed, None }

/// <summary>
/// Writes every step to disk the moment it finishes:
///   steps.csv     one line per step  (result, URL, status, time, failure reason...)
///   checks.csv    one line per check (field, check, expected, actual, PASS/FAIL)
///   payloads\&lt;scenario&gt;\stepN_request.json / _response.json
/// Lines are flushed immediately, so a crash, power cut or Ctrl+C still leaves everything
/// that already ran. Thread-safe (used by parallel scenarios).
/// </summary>
public sealed class ResultStream : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _steps;
    private readonly StreamWriter _checks;
    private readonly string _payloadDir;
    private readonly Masker _mask;
    private readonly PayloadMode _payloads;

    public string Folder { get; }
    public string StepsCsv { get; }
    public string ChecksCsv { get; }

    public ResultStream(string folder, TestSuite suite)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
        _payloadDir = Path.Combine(folder, "payloads");
        _mask = new Masker(suite);
        _payloads = Enum.TryParse<PayloadMode>(suite.Setting("SavePayloads", "All"), ignoreCase: true, out var pm) ? pm : PayloadMode.All;

        StepsCsv = Path.Combine(folder, "steps.csv");
        ChecksCsv = Path.Combine(folder, "checks.csv");
        // UTF-8 with BOM so Excel opens the CSV with the right encoding.
        _steps = new StreamWriter(new FileStream(StepsCsv, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
        _checks = new StreamWriter(new FileStream(ChecksCsv, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(true)) { AutoFlush = true };
        _steps.WriteLine(Csv("Scenario", "Step", "Step Name", "Result", "Method", "URL", "HTTP Status", "Duration (ms)", "Attempts",
            "Checks Passed", "Checks Total", "Captured Values", "Failure Reason / Notes", "Started At"));
        _checks.WriteLine(Csv("Scenario", "Step", "Field Path", "Check", "Expected", "Actual", "Result", "Message"));
    }

    public void Write(StepResult s)
    {
        var captured = string.Join(" | ", s.Captured.Select(c => $"{c.Key}={(_mask.ShouldMask(c.Key) ? _mask.Value(c.Value) : c.Value)}"));
        var notes = string.Join(" | ", new[] { s.FailureReason }.Concat(s.Notes).Where(n => !string.IsNullOrWhiteSpace(n)));
        var stepLine = Csv(s.Def.Scenario, s.Def.Step.ToString(), s.Def.StepName, OutcomeText(s.Outcome), s.Method, _mask.Url(s.Url),
            s.HttpStatus?.ToString() ?? "", s.Outcome == Outcome.Skipped ? "" : s.DurationMs.ToString(), s.Attempts == 0 ? "" : s.Attempts.ToString(),
            s.ChecksPassed.ToString(), s.ChecksTotal.ToString(), captured, notes, s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss.fff"));

        var checkLines = s.Assertions.Select(a => Csv(s.Def.Scenario, s.Def.Step.ToString(), a.Def.FieldPath, a.Def.Check,
            a.Expected, a.Actual, a.Passed ? "PASS" : "FAIL", a.Message)).ToList();

        // Payload files are written outside the lock (different files per step).
        bool savePayload = s.Outcome != Outcome.Skipped && (_payloads == PayloadMode.All ||
                           (_payloads == PayloadMode.Failed && s.Outcome is Outcome.Fail or Outcome.Error));
        if (savePayload) WritePayloads(s);

        lock (_lock)
        {
            _steps.WriteLine(stepLine);
            foreach (var l in checkLines) _checks.WriteLine(l);
        }
    }

    private void WritePayloads(StepResult s)
    {
        try
        {
            var dir = Path.Combine(_payloadDir, Safe(s.Def.Scenario));
            Directory.CreateDirectory(dir);
            var ext = s.Method == "SQL" ? "sql" : "json";
            var enc = new UTF8Encoding(false);
            if (s.RequestBody.Length > 0)
                File.WriteAllText(Path.Combine(dir, $"step{s.Def.Step}_request.{ext}"), _mask.Json(JsonPathUtil.PrettyOrRaw(s.RequestBody)), enc);
            if (s.ResponseBody.Length > 0)
                File.WriteAllText(Path.Combine(dir, $"step{s.Def.Step}_response.json"), _mask.Json(JsonPathUtil.PrettyOrRaw(s.ResponseBody)), enc);
            if (s.RequestHeaders.Length > 0 || s.ResponseHeaders.Length > 0)
                File.WriteAllText(Path.Combine(dir, $"step{s.Def.Step}_headers.txt"),
                    $"URL: {_mask.Url(s.Url)}\n\n--- Request headers ---\n{_mask.Headers(s.RequestHeaders)}\n\n--- Response headers ---\n{_mask.Headers(s.ResponseHeaders)}\n", enc);
        }
        catch (IOException ex)
        {
            // Never let a disk hiccup stop the run.
            s.Notes.Add($"Could not save payload files: {ex.Message}");
        }
    }

    public static string OutcomeText(Outcome o) => o switch
    {
        Outcome.Pass => "PASS", Outcome.Fail => "FAIL", Outcome.Error => "ERROR", _ => "SKIPPED",
    };

    public static string Safe(string s) => Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");

    private static string Csv(params string?[] values) => string.Join(",", values.Select(v =>
    {
        v ??= "";
        // Guard against CSV/formula injection when opened in Excel.
        if (v.Length > 0 && "=+-@".Contains(v[0]) && !decimal.TryParse(v, out _)) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }));

    public void Dispose()
    {
        lock (_lock)
        {
            _steps.Dispose();
            _checks.Dispose();
        }
    }
}
